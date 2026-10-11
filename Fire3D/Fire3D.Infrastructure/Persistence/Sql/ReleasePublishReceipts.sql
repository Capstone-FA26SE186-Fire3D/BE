DO $migration$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;role_name text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 EXECUTE $definition$
-- Forward-only gate. No legacy release receives synthesized readiness or approval.
CREATE OR REPLACE FUNCTION publish_release_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;b buildings;r releases;v scenario_versions;p release_packages;pin release_build_provenance;
 cr scenario_content_reviews;rr revision_reviews;vr validation_runs;a revision_artifacts;m revision_artifacts;stamp timestamptz; receipt release_command_receipts; result jsonb; h text; k text;
BEGIN
 IF p_action<>'Publish' THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 SELECT * INTO r FROM releases WHERE id=p_resource;
 SELECT * INTO b FROM buildings WHERE id=r.building_id;
 IF b.id IS NULL OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO b FROM buildings WHERE id=b.id FOR UPDATE;
 SELECT * INTO r FROM releases WHERE id=p_resource FOR UPDATE;
 stamp:=clock_timestamp();
 IF NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>stamp) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 k:=btrim(p_key);
 IF k IS NULL OR length(k) NOT BETWEEN 1 AND 128 OR k ~ '[[:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('releaseId',r.id,'request',coalesce(p_input,'{}'::jsonb)));
 SELECT * INTO receipt FROM release_command_receipts WHERE actor_id=p_actor AND operation='Publish' AND idempotency_key=k;
 IF receipt.actor_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN
   IF NOT(k=r.id::text AND p_input='{}'::jsonb AND jsonb_typeof(receipt.result)='boolean' AND receipt.input_hash=fet3d_jsonb_payload_hash(jsonb_build_object('releaseId',r.id))) THEN
    RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);
   END IF;
  END IF;
  RETURN jsonb_build_object('code','OK','result',CASE WHEN jsonb_typeof(receipt.result)='boolean' THEN release_representation(r.id) ELSE receipt.result END);
 END IF;
 IF r.status='Published' THEN
  result:=release_representation(r.id);
  INSERT INTO release_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Publish',k,h,result);
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;
 IF r.status<>'Built' THEN RETURN jsonb_build_object('code','RELEASE_NOT_BUILT','status',409); END IF;
 IF NOT EXISTS(SELECT 1 FROM trainings WHERE release_id=r.id AND scenario_version_id=r.scenario_version_id AND organization_id=b.organization_id AND status='Active') THEN RETURN jsonb_build_object('code','TRAINING_INACTIVE','status',409); END IF;
 IF NOT EXISTS(SELECT 1 FROM service_entitlements e JOIN payment_transactions t ON t.id=e.payment_transaction_id AND t.status='Applied'
  WHERE e.building_id=b.id AND e.organization_id=b.organization_id AND e.status='Active' AND e.starts_at<=stamp AND e.ends_at>stamp)
  THEN RETURN jsonb_build_object('code','BUILDING_ENTITLEMENT_REQUIRED','status',409); END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id AND revision_id=r.revision_id AND building_id=b.id;
 SELECT * INTO pin FROM release_build_provenance WHERE release_id=r.id;
 SELECT * INTO cr FROM scenario_content_reviews WHERE id=pin.content_review_id AND scenario_version_id=v.id AND status='Approved' AND content_hash=v.scenario_hash AND rubric_hash=fet3d_jsonb_payload_hash(v.rubric);
 IF cr.id IS NULL THEN RETURN jsonb_build_object('code','CONTENT_APPROVAL_REQUIRED','status',409); END IF;
 SELECT * INTO rr FROM revision_reviews WHERE id=r.confirmation_review_id AND revision_id=v.revision_id AND scenario_version_id=v.id AND action='ConfirmForTraining' AND validation_run_id=pin.validation_run_id;
 SELECT run.* INTO vr FROM validation_runs run JOIN processing_jobs j ON j.id=run.job_id AND j.current_attempt_id=run.processing_attempt_id
  JOIN processing_job_attempts pa ON pa.id=run.processing_attempt_id AND pa.processing_job_id=j.id AND pa.status='Succeeded'
  WHERE run.id=pin.validation_run_id AND run.processing_attempt_id=pin.attempt_id AND run.scenario_version_id=v.id AND run.revision_id=v.revision_id AND run.scenario_hash=v.scenario_hash AND j.status='Succeeded' AND j.kind='ReleasePackage';
 SELECT * INTO p FROM release_packages WHERE release_id=r.id;
 SELECT * INTO a FROM revision_artifacts WHERE id=p.candidate_artifact_id AND id=vr.candidate_artifact_id AND attempt_id=pin.attempt_id AND job_id=vr.job_id AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready;
 SELECT * INTO m FROM revision_artifacts WHERE id=pin.manifest_artifact_id AND attempt_id=a.attempt_id AND job_id=a.job_id AND revision_id=a.revision_id AND artifact_type='manifest' AND is_runtime_ready;
 IF v.id IS NULL OR rr.id IS NULL OR vr.id IS NULL OR rr.annotation_set_id IS DISTINCT FROM vr.annotation_set_id THEN RETURN jsonb_build_object('code','RELEASE_READINESS_REQUIRED','status',409); END IF;
 IF vr.outcome IS DISTINCT FROM 'Passed' THEN RETURN jsonb_build_object('code','RELEASE_QA_NOT_PASSED','status',409); END IF;
 IF EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=vr.id AND severity IN('Error','Critical')) THEN RETURN jsonb_build_object('code','RELEASE_QA_BLOCKERS_PRESENT','status',409); END IF;
 IF a.id IS NULL OR m.id IS NULL THEN RETURN jsonb_build_object('code','RELEASE_ARTIFACT_REQUIRED','status',409); END IF;
 IF p.manifest_url IS DISTINCT FROM m.object_key OR p.manifest_sha256 IS DISTINCT FROM m.sha256_hash OR p.package_url IS DISTINCT FROM a.object_key OR p.checksum_sha256 IS DISTINCT FROM a.sha256_hash OR p.package_size_bytes IS DISTINCT FROM a.size_bytes
  OR p.min_runtime_version IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR p.schema_version IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR p.build_target IS DISTINCT FROM a.metadata->>'buildTarget'
  OR m.metadata IS DISTINCT FROM a.metadata OR package_runtime_compatible(a.metadata,p.min_runtime_version) IS NOT TRUE
  THEN RETURN jsonb_build_object('code','PACKAGE_COMPATIBILITY_REQUIRED','status',409); END IF;
 UPDATE releases SET status='Published',published_at=stamp,published_by=p_actor,updated_at=stamp WHERE id=r.id;
 result:=release_representation(r.id);
 INSERT INTO release_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Publish',k,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Publish','Release',r.id,gen_random_uuid(),jsonb_build_object('status','Built'),jsonb_build_object('status','Published','publishedAt',stamp,'publishedBy',p_actor),stamp);
 RETURN jsonb_build_object('code','OK','result',result);
END $$;
$definition$;
 ALTER FUNCTION publish_release_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION publish_release_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH role_name IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION publish_release_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',role_name); END IF; END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF changed THEN IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user); ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END); END IF; END IF;
END $migration$;
