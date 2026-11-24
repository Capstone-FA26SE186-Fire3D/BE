-- #214 IFC upload hardening. ifc_upload_gate keeps its signature, owner (fet3d_ifc_upload_owner), search_path and EXECUTE grants; the body
-- is the one from IfcUploadReceiptScope.sql (20261007130000_BindIfcReceiptBuilding) with three changes:
--  * Adopt stores quarantine_status 'Accepted' (every upload check passed) instead of the column default 'Pending';
--  * new action 'Reject' (upload-complete refused the content: not STEP, or FILE_SCHEMA not IFC2X3/IFC4/IFC4X3) deletes the Draft
--    revision, the intent and abandoned attempts, queues the staging object for cleanup and audits the rejection (quarantine Rejected);
--  * Claim's completed-intent answer also applies to Reject, so a completed upload is never removed.
-- The owner gets DELETE on the three rows it removes; RLS policies ifc_upload_gate_owner already cover the owner role.
CREATE TEMP TABLE ifc_upload_hardening_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ifc_upload_hardening_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;
GRANT DELETE ON revisions,ifc_upload_intents,ifc_upload_attempts TO fet3d_ifc_upload_owner;

CREATE OR REPLACE FUNCTION ifc_upload_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_attempt uuid DEFAULT NULL,p_etag text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users; b buildings; intent ifc_upload_intents; attempt ifc_upload_attempts; r uuid; s uuid;
 h text:=fet3d_jsonb_payload_hash(p_input); k text; result jsonb;
BEGIN
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR actor.role NOT IN('PlatformAdmin','OrganizationUser') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 IF p_action='Initiate' THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('ifc-upload:'||p_actor||':'||p_key,0));
  SELECT * INTO b FROM buildings WHERE id=p_resource AND deleted_at IS NULL AND is_active;
 ELSE
  SELECT * INTO intent FROM ifc_upload_intents WHERE revision_id=p_resource AND actor_id=p_actor;
  IF intent.revision_id IS NULL THEN RETURN jsonb_build_object('code','IFC_UPLOAD_INTENT_NOT_FOUND','status',404); END IF;
  SELECT * INTO b FROM buildings WHERE id=intent.building_id AND deleted_at IS NULL AND is_active;
 END IF;
 IF b.id IS NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
  OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id)
 THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 -- Re-read under the same resource lock used by archive/update.
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF p_action<>'Initiate' THEN
  SELECT * INTO intent FROM ifc_upload_intents WHERE revision_id=p_resource AND actor_id=p_actor FOR UPDATE;
 END IF;
 IF p_action='Initiate' THEN
  SELECT * INTO intent FROM ifc_upload_intents WHERE actor_id=p_actor AND idempotency_key=p_key;
  IF intent.revision_id IS NOT NULL AND (intent.building_id IS DISTINCT FROM b.id OR intent.input_hash<>h) THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  IF intent.revision_id IS NULL THEN
   r:=gen_random_uuid(); k:='ifc/staging/'||b.organization_id||'/'||b.id||'/'||r||'.ifc';
   INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label,primary_type,status,created_at,updated_at)
    VALUES(r,b.id,b.organization_id,p_actor,p_input->>'versionLabel','IFC','Draft',now(),now());
   INSERT INTO ifc_upload_intents(revision_id,building_id,organization_id,actor_id,idempotency_key,input_hash,staging_key,size,hash,filename,expires_at)
    VALUES(r,b.id,b.organization_id,p_actor,p_key,h,k,(p_input->>'fileSizeBytes')::bigint,p_input->>'sha256Hash',p_input->>'originalFilename',clock_timestamp()+interval '60 minutes') RETURNING * INTO intent;
   INSERT INTO ifc_object_cleanup(object_key,available_at) VALUES(k,intent.expires_at+interval '90 seconds');
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','revisions',r,gen_random_uuid(),now());
  END IF;
 ELSE
  IF p_input->>'objectKey'<>intent.staging_key OR (p_input->>'fileSizeBytes')::bigint<>intent.size
   OR p_input->>'sha256Hash'<>intent.hash OR p_input->>'originalFilename'<>intent.filename OR p_input->>'mimeType'<>'application/octet-stream'
  THEN RETURN jsonb_build_object('code','IFC_UPLOAD_INPUT_CONFLICT','status',409); END IF;
  IF intent.completed_at IS NOT NULL THEN
   IF intent.completion_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
   IF p_action IN('Claim','Reject') THEN RETURN jsonb_build_object('code','IFC_UPLOAD_ALREADY_COMPLETED','status',409); END IF;
   RETURN jsonb_build_object('code','OK','intent',jsonb_build_object('revisionId',intent.revision_id,'buildingId',b.id,'organizationId',b.organization_id,'actorId',p_actor,'stagingKey',intent.staging_key,'size',intent.size,'hash',intent.hash,'filename',intent.filename,'expiresAt',intent.expires_at,'completed',true));
  END IF;
  -- #214: a content check refused bytes whose SHA-256 matches the intent, so this intent can never pass. Remove the Draft revision,
  -- the intent and abandoned attempts; the cleanup worker deletes the staging object and candidate copies once nothing references them.
  -- Allowed after expiry too, so an intent that expired during inspection is still cleaned up.
  IF p_action='Reject' THEN
   IF p_input#>>'{rejection,code}' IS NULL OR p_input#>>'{rejection,code}' NOT IN('IFC_UNSUPPORTED_FORMAT','IFC_UNSUPPORTED_SCHEMA')
    OR jsonb_typeof(p_input#>'{rejection,details}') IS DISTINCT FROM 'object' OR length((p_input#>'{rejection,details}')::text)>4000
   THEN RAISE EXCEPTION 'Unknown IFC upload rejection'; END IF;
   IF EXISTS(SELECT 1 FROM ifc_upload_attempts WHERE revision_id=intent.revision_id AND state='Candidate' AND lease_until>clock_timestamp())
    THEN RETURN jsonb_build_object('code','IFC_UPLOAD_IN_PROGRESS','status',503,'retryAfter',1); END IF;
   UPDATE ifc_object_cleanup SET available_at=clock_timestamp()
    WHERE object_key=intent.staging_key OR object_key IN(SELECT final_key FROM ifc_upload_attempts WHERE revision_id=intent.revision_id);
   DELETE FROM ifc_upload_attempts WHERE revision_id=intent.revision_id;
   DELETE FROM ifc_upload_intents WHERE revision_id=intent.revision_id;
   DELETE FROM revisions WHERE id=intent.revision_id AND status='Draft';
   IF NOT FOUND THEN RAISE EXCEPTION 'Rejected IFC revision is no longer a Draft'; END IF;
   -- QuarantineStatus Rejected: no source document exists for a rejected upload, so the verdict lives in the audit row.
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Reject','revisions',intent.revision_id,gen_random_uuid(),
     jsonb_build_object('operation','IfcUploadRejected','code',p_input#>>'{rejection,code}','quarantineStatus','Rejected','buildingId',b.id,
      'sha256',intent.hash,'fileSizeBytes',intent.size,'filename',intent.filename,'details',p_input#>'{rejection,details}'),now());
   RETURN jsonb_build_object('code','OK');
  END IF;
  IF intent.expires_at<=clock_timestamp() THEN RETURN jsonb_build_object('code','IFC_UPLOAD_EXPIRED','status',410); END IF;
  IF p_action='Claim' THEN
   IF EXISTS(SELECT 1 FROM ifc_upload_attempts WHERE revision_id=intent.revision_id AND state='Candidate' AND lease_until>clock_timestamp())
    THEN RETURN jsonb_build_object('code','IFC_UPLOAD_IN_PROGRESS','status',503,'retryAfter',1); END IF;
   IF NULLIF(p_etag,'') IS NULL THEN RAISE EXCEPTION 'Missing inspected ETag'; END IF;
   r:=gen_random_uuid(); k:='ifc/source/'||intent.organization_id||'/'||intent.revision_id||'/'||r||'.ifc';
   INSERT INTO ifc_upload_attempts(id,revision_id,final_key,source_etag,input_hash,lease_until)
    VALUES(r,intent.revision_id,k,p_etag,h,clock_timestamp()+interval '90 seconds');
   INSERT INTO ifc_object_cleanup(object_key,available_at) VALUES(k,clock_timestamp()+interval '90 seconds');
   RETURN jsonb_build_object('code','OK','attempt',jsonb_build_object('id',r,'finalKey',k));
  ELSIF p_action='Adopt' THEN
   SELECT * INTO attempt FROM ifc_upload_attempts WHERE id=p_attempt AND revision_id=intent.revision_id FOR UPDATE;
   IF attempt.id IS NULL OR attempt.state<>'Candidate' OR attempt.lease_until<=clock_timestamp() OR attempt.input_hash<>h OR attempt.source_etag<>p_etag
    THEN RETURN jsonb_build_object('code','IFC_UPLOAD_ATTEMPT_EXPIRED','status',409); END IF;
   s:=gen_random_uuid();
   INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,file_type,quarantine_status,created_at,upload_verified_at,source_etag)
    VALUES(s,intent.revision_id,p_actor,intent.filename,intent.size,attempt.final_key,'application/octet-stream',intent.hash,'Private','IFC','Accepted',now(),clock_timestamp(),p_etag);
   UPDATE revisions SET status='Uploaded',updated_at=now() WHERE id=intent.revision_id;
   UPDATE ifc_upload_intents SET completed_at=clock_timestamp(),completion_hash=h,final_key=attempt.final_key,source_document_id=s WHERE revision_id=intent.revision_id;
   UPDATE ifc_upload_attempts SET state='Adopted' WHERE id=attempt.id;
   UPDATE ifc_object_cleanup SET available_at=clock_timestamp() WHERE object_key=intent.staging_key;
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','revisions',intent.revision_id,gen_random_uuid(),now());
   RETURN jsonb_build_object('code','OK');
  ELSIF p_action<>'Read' THEN RAISE EXCEPTION 'Unknown IFC operation'; END IF;
 END IF;
 RETURN jsonb_build_object('code','OK','intent',jsonb_build_object('revisionId',intent.revision_id,'buildingId',b.id,'organizationId',b.organization_id,'actorId',p_actor,'stagingKey',intent.staging_key,'size',intent.size,'hash',intent.hash,'filename',intent.filename,'expiresAt',intent.expires_at,'completed',intent.completed_at IS NOT NULL));
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 REVOKE ALL ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM ifc_upload_hardening_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
