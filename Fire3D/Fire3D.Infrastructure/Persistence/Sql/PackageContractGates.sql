-- BE#130 (19/10/2026): release Build (release_access_gate) and Publish (publish_release_gate) required the manifest output and the
-- unity_package output to carry identical metadata. The package worker contract (docs/unity-package-worker.md step 5) lets the
-- unity_package metadata add provenance (agentVersion, fileCount, input hashes, ...), so every contract-conforming package was refused
-- with PACKAGE_METADATA_MISMATCH / PACKAGE_COMPATIBILITY_REQUIRED. Both gates now compare the five contract fields, exactly as the playtest
-- gate does: protocolVersion, manifestSchemaVersion, buildTarget, minRuntimeVersion and requiredCapabilities.
-- BE#131: BuildPackage inserted a job while another job of the revision was Queued/Running, so the unique index one_live_revision_job
-- raised and the API answered 500. It now answers 409 PROCESSING_JOB_ACTIVE with errors.jobId = that job.
-- The functions are the current repo definitions (ReleaseAccess.sql, ReleasePublishReceipts.sql, ScenarioAuthoring.sql) with only these
-- changes; CREATE OR REPLACE keeps their owner and grants.
CREATE TEMP TABLE package_gate_fix_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO package_gate_fix_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;
CREATE OR REPLACE FUNCTION release_access_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;v scenario_versions;r releases;a revision_artifacts;m revision_artifacts;vr validation_runs;cr scenario_content_reviews;rr revision_reviews;
 receipt release_command_receipts;h text;result jsonb;new_id uuid;package_id uuid;
BEGIN
 IF p_action NOT IN('Build','Revoke','GetAccess','Access','Rotate','RevokeCode','Verify','List') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF (p_action IN('Verify','List') AND actor.role NOT IN('Trainee','OrganizationUser','PlatformAdmin')) OR (p_action NOT IN('Verify','List') AND actor.role NOT IN('OrganizationUser','PlatformAdmin')) OR (p_action='Verify' AND actor.role<>'Trainee') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='Build' THEN SELECT * INTO v FROM scenario_versions WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSIF p_action='Revoke' THEN SELECT * INTO r FROM releases WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=r.building_id;
 ELSE SELECT * INTO b FROM buildings WHERE id=p_resource;END IF;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO b FROM buildings WHERE id=b.id;
 IF NOT b.is_active OR b.deleted_at IS NOT NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_action='Build' THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  SELECT * INTO receipt FROM release_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;RETURN jsonb_build_object('code','OK','result',receipt.result);END IF;
  SELECT * INTO cr FROM scenario_content_reviews WHERE scenario_version_id=v.id AND status='Approved' AND content_hash=v.scenario_hash AND rubric_hash=fet3d_jsonb_payload_hash(v.rubric);
  IF cr.id IS NULL THEN RETURN jsonb_build_object('code','CONTENT_APPROVAL_REQUIRED','status',409);END IF;
  SELECT * INTO rr FROM revision_reviews WHERE id=(p_input->>'confirmationReviewId')::uuid AND revision_id=v.revision_id AND scenario_version_id=v.id AND action='ConfirmForTraining';
  SELECT run.* INTO vr FROM validation_runs run JOIN processing_jobs j ON j.id=run.job_id AND j.current_attempt_id=run.processing_attempt_id JOIN processing_job_attempts pa ON pa.id=run.processing_attempt_id
   WHERE run.id=rr.validation_run_id AND run.scenario_version_id=v.id AND run.revision_id=v.revision_id AND run.scenario_hash=v.scenario_hash AND run.outcome='Passed' AND j.status='Succeeded' AND j.kind='ReleasePackage' AND pa.status='Succeeded';
  SELECT * INTO a FROM revision_artifacts WHERE id=vr.candidate_artifact_id AND id=(p_input->>'candidateArtifactId')::uuid AND attempt_id=vr.processing_attempt_id AND job_id=vr.job_id AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready;
  SELECT * INTO m FROM revision_artifacts WHERE attempt_id=a.attempt_id AND job_id=a.job_id AND revision_id=a.revision_id AND artifact_type='manifest' AND is_runtime_ready;
  IF v.revision_id<>(p_input->>'revisionId')::uuid OR a.id IS NULL OR m.id IS NULL OR rr.id IS NULL OR vr.id IS NULL OR EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=vr.id AND severity IN('Error','Critical')) THEN RETURN jsonb_build_object('code','RELEASE_PROVENANCE_MISMATCH','status',409);END IF;
  IF NOT package_runtime_compatible(a.metadata,a.metadata->>'minRuntimeVersion') 
   OR m.metadata->>'protocolVersion' IS DISTINCT FROM a.metadata->>'protocolVersion' OR m.metadata->>'manifestSchemaVersion' IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR m.metadata->>'buildTarget' IS DISTINCT FROM a.metadata->>'buildTarget' OR m.metadata->>'minRuntimeVersion' IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR m.metadata->'requiredCapabilities' IS DISTINCT FROM a.metadata->'requiredCapabilities' THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  IF (p_input->>'manifestUrl' IS NOT NULL AND p_input->>'manifestUrl'<>m.object_key) OR (p_input->>'manifestSha256' IS NOT NULL AND lower(p_input->>'manifestSha256')<>m.sha256_hash)
    OR (p_input->>'packageUrl' IS NOT NULL AND p_input->>'packageUrl'<>a.object_key) OR (p_input->>'checksumSha256' IS NOT NULL AND lower(p_input->>'checksumSha256')<>a.sha256_hash)
    OR (p_input->>'packageSizeBytes' IS NOT NULL AND (p_input->>'packageSizeBytes')::bigint<>a.size_bytes)
    OR (p_input->>'minRuntimeVersion' IS NOT NULL AND p_input->>'minRuntimeVersion'<>a.metadata->>'minRuntimeVersion')
    OR (p_input->>'schemaVersion' IS NOT NULL AND p_input->>'schemaVersion'<>a.metadata->>'manifestSchemaVersion')
    OR (p_input->>'buildTarget' IS NOT NULL AND p_input->>'buildTarget'<>a.metadata->>'buildTarget')
    OR (p_input->>'safetyThresholds' IS NOT NULL AND (p_input->>'safetyThresholds')::jsonb<>v.safety_thresholds)
    THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  IF EXISTS(SELECT 1 FROM releases WHERE revision_id=v.revision_id AND scenario_version_id=v.id) THEN RETURN jsonb_build_object('code','RELEASE_ALREADY_EXISTS','status',409);END IF;
  new_id:=gen_random_uuid();package_id:=gen_random_uuid();
  INSERT INTO releases(id,status,revision_id,scenario_version_id,building_id,organization_id,confirmation_review_id,safety_thresholds) VALUES(new_id,'Built',v.revision_id,v.id,b.id,b.organization_id,rr.id,v.safety_thresholds);
  INSERT INTO release_packages(id,release_id,candidate_artifact_id,manifest_url,manifest_sha256,package_url,checksum_sha256,package_size_bytes,min_runtime_version,schema_version,build_target)
   VALUES(package_id,new_id,a.id,m.object_key,m.sha256_hash,a.object_key,a.sha256_hash,a.size_bytes,a.metadata->>'minRuntimeVersion',a.metadata->>'manifestSchemaVersion',a.metadata->>'buildTarget');
  INSERT INTO release_build_provenance(release_id,validation_run_id,manifest_artifact_id,attempt_id,content_review_id) VALUES(new_id,vr.id,m.id,a.attempt_id,cr.id);
  INSERT INTO trainings(id,status,mode,release_id,scenario_version_id,organization_id,name,description,created_by) VALUES(gen_random_uuid(),'Active','Learn',new_id,v.id,b.organization_id,v.name,v.learner_instructions,p_actor);
  result:=release_representation(new_id);
  INSERT INTO release_command_receipts VALUES(p_actor,p_action,p_key,h,result);
 ELSIF p_action='Revoke' THEN
  IF NULLIF(btrim(p_input->>'reason'),'') IS NULL OR length(p_input->>'reason')>4000 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A reason of 1-4000 characters is required.')));END IF;
  SELECT * INTO r FROM releases WHERE id=p_resource FOR UPDATE;
  IF r.status='Revoked' THEN RETURN jsonb_build_object('code','OK','result',true);END IF;
  UPDATE releases SET status='Revoked',revoked_by=p_actor,revoked_reason=btrim(p_input->>'reason'),revoked_at=now(),updated_at=now() WHERE id=r.id;result:='true'::jsonb;
 ELSIF p_action='List' THEN
  IF actor.role='Trainee' AND b.visibility<>'Public' AND NOT EXISTS(SELECT 1 FROM building_participation_grants WHERE building_id=b.id AND trainee_user_id=p_actor AND access_revision=b.access_revision) THEN RETURN jsonb_build_object('code','BUILDING_ACCESS_REQUIRED','status',403);END IF;
  SELECT COALESCE(jsonb_agg(jsonb_build_object('id',t.id,'releaseId',t.release_id,'name',t.name,'description',t.description,'status',t.status,'startDate',t.start_date,'endDate',t.end_date,'allowedModes',t.allowed_modes,'createdAt',t.created_at) ORDER BY t.created_at DESC,t.id),'[]'::jsonb) INTO result
   FROM trainings t JOIN releases rel ON rel.id=t.release_id JOIN release_build_provenance pin ON pin.release_id=rel.id JOIN scenario_content_reviews review ON review.id=pin.content_review_id AND review.status='Approved'
   WHERE rel.building_id=b.id AND (actor.role<>'Trainee' OR (t.status='Active' AND rel.status='Published' AND (t.start_date IS NULL OR t.start_date<=clock_timestamp()) AND (t.end_date IS NULL OR t.end_date>clock_timestamp())));
  RETURN jsonb_build_object('code','OK','result',result);
 ELSE
  IF p_action IN('Access','Rotate','RevokeCode') THEN
   IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
   IF p_expected<>b.access_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
   IF p_action='Access' THEN
    IF p_input->>'visibility' NOT IN('Private','Public') OR p_input->>'visibility' IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
    UPDATE buildings SET visibility=p_input->>'visibility',updated_at=now() WHERE id=b.id;
   ELSIF p_action='Rotate' THEN
    IF p_input->>'codeHash' IS NULL OR p_input->>'codeHash' !~ '^[0-9a-f]{64}$' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
    UPDATE buildings SET participation_code_hash=p_input->>'codeHash',updated_at=now() WHERE id=b.id;
   ELSE UPDATE buildings SET participation_code_hash=NULL,updated_at=now() WHERE id=b.id;END IF;
   SELECT * INTO b FROM buildings WHERE id=b.id;
  ELSIF p_action='Verify' THEN
   IF b.participation_code_hash IS NULL OR b.participation_code_hash IS DISTINCT FROM p_input->>'codeHash' THEN RETURN jsonb_build_object('code','PARTICIPATION_CODE_INVALID','status',400);END IF;
   INSERT INTO building_participation_grants VALUES(b.id,p_actor,b.access_revision,now()) ON CONFLICT DO NOTHING;
  END IF;
  result:=jsonb_build_object('buildingId',b.id,'visibility',b.visibility,'accessRevision',b.access_revision,'hasParticipationCode',b.participation_code_hash IS NOT NULL);
  IF p_action='GetAccess' THEN RETURN jsonb_build_object('code','OK','result',result);END IF;
 END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update',CASE WHEN p_action IN('Build','Revoke') THEN 'Release' ELSE 'Building' END,CASE WHEN p_action='Build' THEN new_id WHEN p_action='Revoke' THEN r.id ELSE b.id END,gen_random_uuid(),jsonb_build_object('operation',p_action,'accessRevision',b.access_revision),now());
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

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
  OR m.metadata->>'protocolVersion' IS DISTINCT FROM a.metadata->>'protocolVersion' OR m.metadata->>'manifestSchemaVersion' IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR m.metadata->>'buildTarget' IS DISTINCT FROM a.metadata->>'buildTarget' OR m.metadata->>'minRuntimeVersion' IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR m.metadata->'requiredCapabilities' IS DISTINCT FROM a.metadata->'requiredCapabilities' OR package_runtime_compatible(a.metadata,p.min_runtime_version) IS NOT TRUE
  THEN RETURN jsonb_build_object('code','PACKAGE_COMPATIBILITY_REQUIRED','status',409); END IF;
 UPDATE releases SET status='Published',published_at=stamp,published_by=p_actor,updated_at=stamp WHERE id=r.id;
 result:=release_representation(r.id);
 INSERT INTO release_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Publish',k,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Publish','Release',r.id,gen_random_uuid(),jsonb_build_object('status','Built'),jsonb_build_object('status','Published','publishedAt',stamp,'publishedBy',p_actor),stamp);
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

CREATE OR REPLACE FUNCTION scenario_authoring_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_revision bigint DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;s scenarios;d scenario_drafts;v scenario_versions;r revisions;src source_documents;receipt authoring_command_receipts;
 h text:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input,'revision',p_revision));result_uuid uuid;n integer;rev bigint;job uuid;pin jsonb;issues jsonb;
BEGIN
 IF p_action<>'UpdateDraft' AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 IF p_action='CreateScenario' THEN SELECT * INTO b FROM buildings WHERE id=p_resource;
 ELSIF p_action='CreateDraft' THEN SELECT * INTO s FROM scenarios WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 ELSIF p_action='BuildPackage' THEN SELECT * INTO v FROM scenario_versions WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=v.scenario_id;SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSE SELECT * INTO d FROM scenario_drafts WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=d.scenario_id;SELECT * INTO b FROM buildings WHERE id=d.building_id; END IF;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF p_key IS NOT NULL THEN PERFORM pg_advisory_xact_lock(hashtextextended('authoring:'||p_actor||':'||p_action||':'||p_key,0)); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF s.id IS NOT NULL THEN SELECT * INTO s FROM scenarios WHERE scenarios.id=s.id FOR UPDATE; END IF;
 SELECT * INTO receipt FROM authoring_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
 IF receipt.result_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  RETURN jsonb_build_object('code','OK','id',receipt.result_id);
 END IF;
 IF p_action='CreateScenario' THEN
  IF NULLIF(btrim(p_input->>'name'),'') IS NULL OR length(p_input->>'name')>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('name',jsonb_build_array('Name must contain 1-255 characters.'))); END IF;
  result_uuid:=gen_random_uuid();INSERT INTO scenarios(id,building_id,organization_id,name,created_by,created_at) VALUES(result_uuid,b.id,b.organization_id,btrim(p_input->>'name'),p_actor,now());
 ELSIF p_action='CreateDraft' THEN
  SELECT * INTO r FROM revisions WHERE revisions.id=(p_input->>'revisionId')::uuid AND building_id=b.id AND organization_id=b.organization_id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('revisionId',jsonb_build_array('Revision must belong to this Building.'))); END IF;
  SELECT COALESCE(max(draft_number),0)+1 INTO n FROM scenario_drafts WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();INSERT INTO scenario_drafts(id,scenario_id,revision_id,organization_id,building_id,draft_number,state,source,created_by,created_at,updated_at) VALUES(result_uuid,s.id,r.id,b.organization_id,b.id,n,'{}','manual',p_actor,now(),now());
 ELSIF p_action IN('UpdateDraft','Snapshot') THEN
  SELECT * INTO d FROM scenario_drafts WHERE scenario_drafts.id=p_resource FOR UPDATE;
  SELECT xmin::text::bigint INTO rev FROM scenario_drafts WHERE scenario_drafts.id=p_resource;
  IF p_revision IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428); END IF;
  IF rev<>p_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412); END IF;
  IF p_action='UpdateDraft' THEN
   UPDATE scenario_drafts SET state=p_input,updated_at=now() WHERE scenario_drafts.id=d.id RETURNING xmin::text::bigint INTO rev;
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','ScenarioDraft',d.id,gen_random_uuid(),jsonb_build_object('revision',rev,'stateHash',fet3d_jsonb_payload_hash(p_input)),now());
   RETURN jsonb_build_object('code','OK','revision',rev);
  END IF;
  IF jsonb_typeof(d.state->'rubric') IS DISTINCT FROM 'object' OR jsonb_typeof(d.state->'learningObjectives') IS DISTINCT FROM 'array' OR jsonb_array_length(d.state->'learningObjectives')=0 OR NULLIF(btrim(d.state->>'learnerInstructions'),'') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Validate the v7 rubric and learner fields before snapshot.'))); END IF;
  issues:=scenario_reference_issues(d.revision_id,d.state);
  IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Anchor or runtime capability is not accepted for this revision.')));END IF;
  SELECT COALESCE(max(version_number),0)+1 INTO n FROM scenario_versions WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();
  INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,scenario_hash,created_by,created_at,state_snapshot,learning_objectives,learner_instructions,rubric)
   VALUES(result_uuid,s.id,d.revision_id,b.id,b.organization_id,n,COALESCE(NULLIF(d.state->>'name',''),s.name),'7','1',COALESCE((d.state->>'randomSeed')::bigint,0),(d.state->'scoringConfig'->>'timeLimitSeconds')::integer,d.state->'spawnPoints',COALESCE(NULLIF(d.state->'goals','null'::jsonb),'[]'),d.state->'hazards',COALESCE(NULLIF(d.state->'npcs','null'::jsonb),'[]'),COALESCE(NULLIF(d.state->'blockedElements','null'::jsonb),'[]'),d.state->'routingConfig',d.state->'scoringConfig',COALESCE(NULLIF(d.state->'modePolicy','null'::jsonb),'{}'),COALESCE(NULLIF(d.state->'safetyThresholds','null'::jsonb),'{}'),COALESCE((d.state->>'replanIntervalSeconds')::integer,0),fet3d_jsonb_payload_hash(d.state),p_actor,now(),d.state,d.state->'learningObjectives',d.state->>'learnerInstructions',d.state->'rubric');
 ELSIF p_action='BuildPackage' THEN
  IF v.state_snapshot IS NULL THEN RETURN jsonb_build_object('code','SCENARIO_SNAPSHOT_REQUIRED','status',409); END IF;
  SELECT * INTO src FROM source_documents WHERE revision_id=v.revision_id AND upload_verified_at IS NOT NULL;
  IF src.id IS NULL THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
  IF p_input->>'kind' IS NULL OR p_input->>'kind' NOT IN('PlaytestPackage','ReleasePackage') OR NULLIF(p_input->>'buildTarget','') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400); END IF;
  pin:=jsonb_build_object('scenarioVersionId',v.id,'scenarioHash',v.scenario_hash,'rubricHash',fet3d_jsonb_payload_hash(v.rubric),'sourceId',src.id,'sourceHash',src.sha256_hash,'kind',p_input->>'kind','buildTarget',p_input->>'buildTarget');
  SELECT processing_jobs.id INTO result_uuid FROM processing_jobs WHERE revision_id=v.revision_id AND scenario_version_id=v.id AND kind=p_input->>'kind' AND input_hash=fet3d_jsonb_payload_hash(pin);
  IF result_uuid IS NULL THEN
   -- One Queued/Running job per revision (unique index one_live_revision_job): answer 409 with that job instead of a 500.
   SELECT j.id INTO job FROM processing_jobs j WHERE j.revision_id=v.revision_id AND j.status IN('Queued','Running') ORDER BY j.created_at LIMIT 1;
   IF job IS NOT NULL THEN RETURN jsonb_build_object('code','PROCESSING_JOB_ACTIVE','status',409,'errors',jsonb_build_object('jobId',jsonb_build_array(job::text))); END IF;
   result_uuid:=gen_random_uuid();INSERT INTO processing_jobs(id,revision_id,source_document_id,scenario_version_id,kind,job_key,input_hash,input_snapshot,status,created_at) VALUES(result_uuid,v.revision_id,src.id,v.id,p_input->>'kind',gen_random_uuid(),fet3d_jsonb_payload_hash(pin),pin,'Queued',now());
   PERFORM enqueue_integration_outbox_event('package-build:'||result_uuid,'ProcessingJob',result_uuid,'ProcessingJobRequested','1',jsonb_build_object('job_id',result_uuid,'input_hash',fet3d_jsonb_payload_hash(pin),'input_snapshot',pin));
  END IF;
 ELSE RETURN jsonb_build_object('code','AUTHORING_ACTION_INVALID','status',400); END IF;
 INSERT INTO authoring_command_receipts(actor_id,operation,idempotency_key,input_hash,result_id) VALUES(p_actor,p_action,p_key,h,result_uuid);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create',CASE p_action WHEN 'CreateScenario' THEN 'Scenario' WHEN 'CreateDraft' THEN 'ScenarioDraft' WHEN 'Snapshot' THEN 'ScenarioVersion' ELSE 'ProcessingJob' END,result_uuid,gen_random_uuid(),jsonb_build_object('operation',p_action),now());
 RETURN jsonb_build_object('code','OK','id',result_uuid);
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM package_gate_fix_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
