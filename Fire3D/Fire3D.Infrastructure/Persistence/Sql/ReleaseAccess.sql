
-- No legacy release is backfilled with approval/provenance.
ALTER TABLE buildings ADD COLUMN visibility text NOT NULL DEFAULT 'Private' CHECK(visibility IN('Private','Public'));
ALTER TABLE buildings ADD COLUMN access_revision bigint NOT NULL DEFAULT 1 CHECK(access_revision>0);
ALTER TABLE buildings ADD COLUMN participation_code_hash varchar(64) CHECK(participation_code_hash ~ '^[0-9a-f]{64}$');
CREATE TABLE building_participation_grants(building_id uuid NOT NULL REFERENCES buildings(id),trainee_user_id uuid NOT NULL REFERENCES users(id),access_revision bigint NOT NULL,granted_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(building_id,trainee_user_id,access_revision));
CREATE TABLE release_build_provenance(release_id uuid PRIMARY KEY REFERENCES releases(id),validation_run_id uuid NOT NULL REFERENCES validation_runs(id),manifest_artifact_id uuid NOT NULL REFERENCES revision_artifacts(id),attempt_id uuid NOT NULL REFERENCES processing_job_attempts(id),content_review_id uuid NOT NULL REFERENCES scenario_content_reviews(id),created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE release_command_receipts(actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,input_hash varchar(64) NOT NULL,result jsonb NOT NULL,PRIMARY KEY(actor_id,operation,idempotency_key));
DO $$ DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['building_participation_grants','release_build_provenance','release_command_receipts'] LOOP
 EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('CREATE POLICY release_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['releases','release_packages','trainings'] LOOP
 EXECUTE format('CREATE POLICY release_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
END $$;
GRANT SELECT,INSERT ON building_participation_grants,release_build_provenance,release_command_receipts TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT,UPDATE ON releases TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT ON release_packages,trainings TO fet3d_ifc_upload_owner;
GRANT UPDATE(visibility,access_revision,participation_code_hash,updated_at) ON buildings TO fet3d_ifc_upload_owner;
CREATE FUNCTION guard_building_access_revision() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
 IF NEW.visibility IS DISTINCT FROM OLD.visibility OR NEW.participation_code_hash IS DISTINCT FROM OLD.participation_code_hash THEN NEW.access_revision:=OLD.access_revision+1;
 ELSIF NEW.access_revision IS DISTINCT FROM OLD.access_revision THEN RAISE EXCEPTION 'Access revision is server-managed';END IF;RETURN NEW;
END $$;
CREATE TRIGGER building_access_revision BEFORE UPDATE ON buildings FOR EACH ROW EXECUTE FUNCTION guard_building_access_revision();
CREATE FUNCTION immutable_release_package() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Release package provenance is immutable';END $$;
CREATE TRIGGER immutable_release_package BEFORE UPDATE OR DELETE ON release_packages FOR EACH ROW EXECUTE FUNCTION immutable_release_package();
CREATE TRIGGER immutable_release_provenance BEFORE UPDATE OR DELETE ON release_build_provenance FOR EACH ROW EXECUTE FUNCTION immutable_release_package();
CREATE FUNCTION release_representation(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',r.id,'revisionId',r.revision_id,'scenarioVersionId',r.scenario_version_id,'buildingId',r.building_id,'organizationId',r.organization_id,'confirmationReviewId',r.confirmation_review_id,'status',r.status,'safetyThresholds',r.safety_thresholds::text,'publishedBy',r.published_by,'publishedAt',r.published_at,'revokedBy',r.revoked_by,'revokedReason',r.revoked_reason,'revokedAt',r.revoked_at,'createdAt',r.created_at,'updatedAt',r.updated_at,
 'package',jsonb_build_object('id',p.id,'candidateArtifactId',p.candidate_artifact_id,'manifestUrl',p.manifest_url,'manifestSha256',p.manifest_sha256,'packageUrl',p.package_url,'checksumSha256',p.checksum_sha256,'packageSizeBytes',p.package_size_bytes,'minRuntimeVersion',p.min_runtime_version,'schemaVersion',p.schema_version,'buildTarget',p.build_target))
 FROM releases r JOIN release_packages p ON p.release_id=r.id WHERE r.id=p_id
$$;
CREATE FUNCTION release_access_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
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
  IF NOT package_runtime_compatible(a.metadata,a.metadata->>'minRuntimeVersion') OR m.metadata IS DISTINCT FROM a.metadata THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
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
DO $$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;sig text;r text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user);END IF;GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','release_representation(uuid)'] LOOP EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON releases,release_packages,trainings,building_participation_grants,release_build_provenance,release_command_receipts FROM %I',r);END IF;END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF changed THEN IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
