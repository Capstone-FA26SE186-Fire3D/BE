-- Additive: legacy source documents remain unverified. No object key or metadata is backfilled.
ALTER TABLE source_documents ADD COLUMN upload_verified_at timestamptz, ADD COLUMN source_etag text;
CREATE TABLE ifc_upload_intents (
 revision_id uuid PRIMARY KEY REFERENCES revisions(id), building_id uuid NOT NULL REFERENCES buildings(id),
 organization_id uuid NOT NULL REFERENCES organizations(id), actor_id uuid NOT NULL REFERENCES users(id),
 idempotency_key varchar(128) NOT NULL, input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),
 staging_key text NOT NULL UNIQUE, size bigint NOT NULL CHECK(size>0), hash varchar(64) NOT NULL CHECK(hash ~ '^[0-9a-f]{64}$'),
 filename varchar(255) NOT NULL, expires_at timestamptz NOT NULL, completed_at timestamptz,
 completion_hash varchar(64), final_key text, source_document_id uuid REFERENCES source_documents(id),
 UNIQUE(actor_id,idempotency_key), CHECK((completed_at IS NULL)=(source_document_id IS NULL))
);
CREATE TABLE ifc_upload_attempts (
 id uuid PRIMARY KEY, revision_id uuid NOT NULL REFERENCES ifc_upload_intents(revision_id), final_key text NOT NULL UNIQUE,
 source_etag text NOT NULL, input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),
 lease_until timestamptz NOT NULL, state text NOT NULL DEFAULT 'Candidate' CHECK(state IN('Candidate','Adopted','Abandoned')),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ifc_candidate_recovery ON ifc_upload_attempts(state,lease_until);
CREATE TABLE ifc_object_cleanup (
 object_key text PRIMARY KEY, available_at timestamptz NOT NULL, lease_token uuid, lease_until timestamptz,
 attempts integer NOT NULL DEFAULT 0, cleaned_at timestamptz, last_error text
);
-- Tombstones are retained/rechecked: lease expiry or DELETE response is not proof a timed-out S3 copy cannot finish later.
CREATE INDEX ifc_cleanup_due ON ifc_object_cleanup(available_at,lease_until);

DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_ifc_upload_owner') THEN
 CREATE ROLE fet3d_ifc_upload_owner NOLOGIN NOSUPERUSER NOBYPASSRLS; END IF; END $$;
-- Preserve the migration identity's original membership and schema grant.
CREATE TEMP TABLE ifc_upload_permission_state(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE original_set boolean; original_inherit boolean; changed boolean; had_create boolean; BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_ifc_upload_owner' AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN
  RAISE EXCEPTION 'IFC owner must be a restricted NOLOGIN role'; END IF;
 SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 INSERT INTO ifc_upload_permission_state VALUES(original_set,original_inherit,changed,had_create);
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;
GRANT SELECT ON users,organizations,buildings TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT,UPDATE ON revisions,source_documents,ifc_upload_intents,ifc_upload_attempts,ifc_object_cleanup TO fet3d_ifc_upload_owner;
GRANT INSERT ON audit_logs TO fet3d_ifc_upload_owner;
GRANT EXECUTE ON FUNCTION fet3d_jsonb_payload_hash(jsonb) TO fet3d_ifc_upload_owner;
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['users','organizations','buildings','revisions','source_documents','ifc_upload_intents','ifc_upload_attempts','ifc_object_cleanup','audit_logs'] LOOP
  EXECUTE format('CREATE POLICY ifc_upload_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['ifc_upload_intents','ifc_upload_attempts','ifc_object_cleanup'] LOOP
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
 END LOOP;
END $$;

CREATE FUNCTION ifc_upload_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_attempt uuid DEFAULT NULL,p_etag text DEFAULT NULL)
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
  IF intent.revision_id IS NOT NULL AND intent.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
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
   IF p_action='Claim' THEN RETURN jsonb_build_object('code','IFC_UPLOAD_ALREADY_COMPLETED','status',409); END IF;
   RETURN jsonb_build_object('code','OK','intent',jsonb_build_object('revisionId',intent.revision_id,'buildingId',b.id,'organizationId',b.organization_id,'actorId',p_actor,'stagingKey',intent.staging_key,'size',intent.size,'hash',intent.hash,'filename',intent.filename,'expiresAt',intent.expires_at,'completed',true));
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
    VALUES(s,intent.revision_id,p_actor,intent.filename,intent.size,attempt.final_key,'application/octet-stream',intent.hash,'Private','IFC','Pending',now(),clock_timestamp(),p_etag);
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
ALTER FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) OWNER TO fet3d_ifc_upload_owner;
REVOKE ALL ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) TO fet3d_backend_executor;
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN GRANT EXECUTE ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text) TO fire3d_api; END IF; END $$;

REVOKE INSERT,UPDATE,DELETE ON source_documents FROM fet3d_backend_executor;
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN REVOKE INSERT,UPDATE,DELETE ON source_documents FROM fire3d_api; END IF; END $$;

CREATE FUNCTION claim_ifc_object_cleanup() RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE job ifc_object_cleanup; token uuid:=gen_random_uuid();
BEGIN
 UPDATE ifc_upload_attempts SET state='Abandoned' WHERE state='Candidate' AND lease_until<=clock_timestamp();
 SELECT * INTO job FROM ifc_object_cleanup WHERE available_at<=clock_timestamp() AND (lease_until IS NULL OR lease_until<=clock_timestamp())
  ORDER BY available_at,object_key LIMIT 1 FOR UPDATE SKIP LOCKED;
 IF job.object_key IS NULL THEN RETURN NULL; END IF;
 IF EXISTS(SELECT 1 FROM source_documents WHERE storage_url=job.object_key)
  OR EXISTS(SELECT 1 FROM ifc_upload_attempts WHERE final_key=job.object_key AND state='Candidate' AND lease_until>clock_timestamp())
  OR EXISTS(SELECT 1 FROM ifc_upload_intents WHERE staging_key=job.object_key AND completed_at IS NULL AND expires_at>clock_timestamp())
 THEN UPDATE ifc_object_cleanup SET available_at=clock_timestamp()+interval '15 minutes' WHERE object_key=job.object_key;
  RETURN NULL;
 END IF;
 UPDATE ifc_object_cleanup SET lease_token=token,lease_until=clock_timestamp()+interval '60 seconds',attempts=attempts+1 WHERE object_key=job.object_key;
 RETURN jsonb_build_object('key',job.object_key,'token',token,'attempt',job.attempts+1);
END $$;
CREATE FUNCTION finish_ifc_object_cleanup(p_key text,p_token uuid,p_success boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
BEGIN
 UPDATE ifc_object_cleanup SET lease_token=NULL,lease_until=NULL,
  available_at=clock_timestamp()+make_interval(secs=>CASE WHEN p_success THEN 3600 ELSE least(1800,30*(2^least(attempts,6))::integer) END),
  cleaned_at=CASE WHEN p_success THEN clock_timestamp() ELSE cleaned_at END,last_error=CASE WHEN p_success THEN NULL ELSE 'STORAGE_UNAVAILABLE' END
 WHERE object_key=p_key AND lease_token=p_token AND lease_until>clock_timestamp();
 RETURN FOUND;
END $$;
-- These gates are for BE recovery only. Workers have no direct cleanup/provenance DML.

ALTER FUNCTION claim_ifc_object_cleanup() OWNER TO fet3d_ifc_upload_owner;
ALTER FUNCTION finish_ifc_object_cleanup(text,uuid,boolean) OWNER TO fet3d_ifc_upload_owner;
REVOKE ALL ON FUNCTION claim_ifc_object_cleanup(),finish_ifc_object_cleanup(text,uuid,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION claim_ifc_object_cleanup(),finish_ifc_object_cleanup(text,uuid,boolean) TO fet3d_backend_executor;
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
 GRANT EXECUTE ON FUNCTION claim_ifc_object_cleanup(),finish_ifc_object_cleanup(text,uuid,boolean) TO fire3d_api;
 END IF; END $$;
DO $$ DECLARE state record; BEGIN
 SELECT * INTO state FROM ifc_upload_permission_state;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s, INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $$;
DROP TABLE ifc_upload_permission_state;
