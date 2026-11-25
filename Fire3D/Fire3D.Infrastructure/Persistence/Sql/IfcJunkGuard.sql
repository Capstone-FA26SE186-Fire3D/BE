-- #219 junk IFC guard (BE layer 3 of 4). Two gates change; both stay owned by fet3d_ifc_upload_owner.
--  * ifc_upload_gate (signature, search_path and grants unchanged): body from IfcUploadHardening.sql (20261123110000_IfcUploadHardening)
--    plus (1) Initiate refuses a SHA-256 whose latest Geometry run in the Organization failed on an Error/Critical issue with
--    409 IFC_SOURCE_ALREADY_FAILED before any revision exists, and audits it; (2) Reject also accepts IFC_PRECHECK_FAILED.
--  * request_revision_processing gains p_daily_limit (IfcProcessing:DailyLimitPerOrganization from the API). The 3-argument version
--    from ProcessingWorker.sql (20261006130000_AddProcessingWorkerGates) is dropped so no caller can skip the limits. Body otherwise as
--    there, plus before a new Geometry job: an Organization advisory lock, 409 IFC_ORG_JOB_RUNNING while a Geometry job of the
--    Organization is Queued/Running, 429 IFC_PROCESS_DAILY_LIMIT (retryAfter = seconds to the next UTC midnight) when the Organization
--    already created p_daily_limit Geometry jobs this UTC day. Each refusal is audited (action Reject).
--  * ifc_processing_policies (team decision 24/11/2026): the daily limit is a versioned policy PlatformAdmin edits (default and per
--    Organization); p_daily_limit is only the starting value when no policy exists. ifc_processing_policy_gate serves the admin and
--    Organization routes.
-- The owner already reads processing_jobs, validation_runs and revision_issues (ProcessingWorker.sql).
CREATE TEMP TABLE ifc_junk_guard_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 INSERT INTO ifc_junk_guard_permissions VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 -- ALTER FUNCTION ... OWNER needs CREATE on the schema for the new owner; restored below.
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION ifc_upload_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_attempt uuid DEFAULT NULL,p_etag text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users; b buildings; intent ifc_upload_intents; attempt ifc_upload_attempts; r uuid; s uuid; failed_toolchain text; current_toolchain text;
 h text:=fet3d_jsonb_payload_hash(p_input); k text; result jsonb; failed_job uuid; failed_run uuid; failed_outcome text; qa jsonb;
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
   -- #219: the latest Geometry run of this SHA-256 in the Organization failed on a blocking (Error/Critical) QA issue. The same bytes
   -- would fail again, so no revision, intent or worker run is spent; the answer names that run's blocking QA codes.
   SELECT j.id,vr.id,vr.outcome,pa.toolchain_version INTO failed_job,failed_run,failed_outcome,failed_toolchain
    FROM processing_jobs j JOIN revisions rv ON rv.id=j.revision_id JOIN buildings bb ON bb.id=rv.building_id
    JOIN validation_runs vr ON vr.job_id=j.id AND vr.processing_attempt_id=j.current_attempt_id
    JOIN processing_job_attempts pa ON pa.id=j.current_attempt_id
    WHERE bb.organization_id=b.organization_id AND j.kind='Geometry' AND j.input_hash=lower(p_input->>'sha256Hash')
    ORDER BY vr.created_at DESC,vr.id DESC LIMIT 1;
   -- Only while the same Geometry worker version would judge the bytes again: after a worker release (newest Geometry attempt has another
   -- toolchain_version) the file gets a new chance, so a worker bug that failed valid models never locks them out.
   SELECT a.toolchain_version INTO current_toolchain FROM processing_job_attempts a JOIN processing_jobs jj ON jj.id=a.processing_job_id
    WHERE jj.kind='Geometry' ORDER BY a.created_at DESC,a.id DESC LIMIT 1;
   IF failed_outcome='Failed' AND failed_toolchain IS NOT DISTINCT FROM current_toolchain THEN
    SELECT COALESCE(jsonb_agg(DISTINCT code ORDER BY code),'[]'::jsonb) INTO qa FROM revision_issues WHERE validation_run_id=failed_run AND severity IN('Error','Critical');
    IF jsonb_array_length(qa)>0 THEN
     INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
      VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Reject','buildings',b.id,gen_random_uuid(),
       jsonb_build_object('operation','IfcUploadRejected','code','IFC_SOURCE_ALREADY_FAILED','sha256',lower(p_input->>'sha256Hash'),
        'failedJobId',failed_job,'validationRunId',failed_run,'qaCodes',qa),now());
     RETURN jsonb_build_object('code','IFC_SOURCE_ALREADY_FAILED','status',409,'errors',jsonb_build_object('qaCodes',qa,
      'jobId',jsonb_build_array(failed_job::text),'validationRunId',jsonb_build_array(failed_run::text)));
    END IF;
   END IF;
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
  -- #214/#219: a content check (STEP header, FILE_SCHEMA, entity precheck) refused bytes whose SHA-256 matches the intent, so this intent can never pass. Remove the Draft revision,
  -- the intent and abandoned attempts; the cleanup worker deletes the staging object and candidate copies once nothing references them.
  -- Allowed after expiry too, so an intent that expired during inspection is still cleaned up.
  IF p_action='Reject' THEN
   IF p_input#>>'{rejection,code}' IS NULL OR p_input#>>'{rejection,code}' NOT IN('IFC_UNSUPPORTED_FORMAT','IFC_UNSUPPORTED_SCHEMA','IFC_PRECHECK_FAILED')
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


-- #219 (team decision 24/11/2026): the daily number of new Geometry jobs is a versioned policy that PlatformAdmin edits, modelled on the
-- AI quota policies (immutable rows with an effective time). A row with organization_id NULL is the default for every Organization; a
-- row for one Organization overrides it (daily_limit NULL there means "back to the default"). The newest row in effect wins; without
-- any row the API's IfcProcessing:DailyLimitPerOrganization (default 20) applies. policy_kind leaves room for selling extra runs later.
CREATE TABLE ifc_processing_policies(
 id uuid PRIMARY KEY,
 organization_id uuid REFERENCES organizations(id),
 policy_kind text NOT NULL DEFAULT 'DailyGeometryRuns' CHECK(policy_kind IN('DailyGeometryRuns')),
 daily_limit integer CHECK(daily_limit IS NULL OR daily_limit BETWEEN 0 AND 100000),
 effective_from timestamptz NOT NULL,
 note text CHECK(note IS NULL OR length(note) BETWEEN 1 AND 500),
 created_by uuid NOT NULL REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 CONSTRAINT ifc_processing_default_has_limit CHECK(organization_id IS NOT NULL OR daily_limit IS NOT NULL)
);
CREATE INDEX ifc_processing_policies_lookup ON ifc_processing_policies(organization_id,effective_from DESC,created_at DESC);
CREATE FUNCTION immutable_ifc_processing_policy() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'IFC processing policies are immutable; add a new version'; END $$;
CREATE TRIGGER ifc_processing_policy_immutable BEFORE UPDATE OR DELETE ON ifc_processing_policies FOR EACH ROW EXECUTE FUNCTION immutable_ifc_processing_policy();

-- Effective limit of one Organization now: {dailyLimit, source Organization|Default|Configuration, policyId}.
CREATE FUNCTION ifc_processing_effective_limit(p_org uuid,p_fallback integer) RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog,public AS $$
DECLARE o ifc_processing_policies;d ifc_processing_policies;
BEGIN
 SELECT * INTO o FROM ifc_processing_policies WHERE organization_id=p_org AND effective_from<=clock_timestamp() ORDER BY effective_from DESC,created_at DESC,id LIMIT 1;
 IF o.id IS NOT NULL AND o.daily_limit IS NOT NULL THEN RETURN jsonb_build_object('dailyLimit',o.daily_limit,'source','Organization','policyId',o.id);END IF;
 SELECT * INTO d FROM ifc_processing_policies WHERE organization_id IS NULL AND effective_from<=clock_timestamp() ORDER BY effective_from DESC,created_at DESC,id LIMIT 1;
 IF d.id IS NOT NULL THEN RETURN jsonb_build_object('dailyLimit',d.daily_limit,'source','Default','policyId',d.id);END IF;
 RETURN jsonb_build_object('dailyLimit',p_fallback,'source','Configuration','policyId',NULL);
END $$;

-- Limit and use today (UTC) of one Organization, as the 429 and the usage routes report them.
CREATE FUNCTION ifc_processing_usage(p_org uuid,p_fallback integer) RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog,public AS $$
DECLARE lim jsonb:=ifc_processing_effective_limit(p_org,p_fallback);day_start timestamptz:=date_trunc('day',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';used integer;
BEGIN
 SELECT count(*) INTO used FROM processing_jobs j JOIN revisions r ON r.id=j.revision_id JOIN buildings b ON b.id=r.building_id
  WHERE b.organization_id=p_org AND j.kind='Geometry' AND j.created_at>=day_start;
 RETURN lim||jsonb_build_object('organizationId',p_org,'used',used,'remaining',GREATEST(0,(lim->>'dailyLimit')::int-used),'resetsAt',day_start+interval '1 day');
END $$;

-- PlatformAdmin: ListPolicies (input organizationId?, page, pageSize), CreatePolicy (input organizationId?, dailyLimit, effectiveFrom?, note?),
-- Usage (resource = Organization). OrganizationUser: Usage of the own Organization (resource ignored).
CREATE FUNCTION ifc_processing_policy_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;org uuid;page_number integer;page_size integer;total integer;result jsonb;p ifc_processing_policies;lim integer;eff timestamptz;fallback integer;
BEGIN
 IF p_action NOT IN('ListPolicies','CreatePolicy','Usage') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 fallback:=COALESCE((p_input->>'fallback')::int,20);
 IF p_action='Usage' THEN
  IF actor.role::text='OrganizationUser' THEN org:=actor.organization_id;
  ELSIF actor.role::text='PlatformAdmin' THEN org:=p_resource;
  ELSE RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  IF org IS NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=org AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ifc_processing_usage(org,fallback));
 END IF;
 IF actor.role::text<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 org:=(p_input->>'organizationId')::uuid;
 IF org IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=org AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='ListPolicies' THEN
  page_number:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF page_number<1 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  SELECT count(*) INTO total FROM ifc_processing_policies WHERE (p_input->>'organizationId' IS NULL AND p_input->>'scope'='Default' AND organization_id IS NULL)
   OR (org IS NOT NULL AND organization_id=org) OR (p_input->>'organizationId' IS NULL AND COALESCE(p_input->>'scope','All')='All');
  SELECT COALESCE(jsonb_agg(jsonb_build_object('id',x.id,'organizationId',x.organization_id,'policyKind',x.policy_kind,'dailyLimit',x.daily_limit,
    'effectiveFrom',x.effective_from,'note',x.note,'createdBy',x.created_by,'createdAt',x.created_at) ORDER BY x.effective_from DESC,x.created_at DESC,x.id),'[]'::jsonb) INTO result
  FROM (SELECT * FROM ifc_processing_policies WHERE (p_input->>'organizationId' IS NULL AND p_input->>'scope'='Default' AND organization_id IS NULL)
    OR (org IS NOT NULL AND organization_id=org) OR (p_input->>'organizationId' IS NULL AND COALESCE(p_input->>'scope','All')='All')
   ORDER BY effective_from DESC,created_at DESC,id LIMIT page_size OFFSET (page_number::bigint-1)*page_size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size,
   'effectiveDefault',ifc_processing_effective_limit(NULL,fallback)));
 END IF;
 -- CreatePolicy: a new version; the previous ones stay as history.
 lim:=(p_input->>'dailyLimit')::int;eff:=COALESCE((p_input->>'effectiveFrom')::timestamptz,clock_timestamp());
 IF (lim IS NULL AND org IS NULL) OR lim NOT BETWEEN 0 AND 100000 OR eff<clock_timestamp()-interval '1 minute'
  OR (p_input->>'note' IS NOT NULL AND length(btrim(p_input->>'note')) NOT BETWEEN 1 AND 500) THEN
  RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 INSERT INTO ifc_processing_policies(id,organization_id,daily_limit,effective_from,note,created_by)
 VALUES(gen_random_uuid(),org,lim,eff,NULLIF(btrim(p_input->>'note'),''),p_actor) RETURNING * INTO p;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,org,'User','Create','IfcProcessingPolicy',p.id,gen_random_uuid(),
  jsonb_build_object('organizationId',org,'dailyLimit',lim,'effectiveFrom',eff),now());
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('id',p.id,'organizationId',p.organization_id,'policyKind',p.policy_kind,'dailyLimit',p.daily_limit,
  'effectiveFrom',p.effective_from,'note',p.note,'createdBy',p.created_by,'createdAt',p.created_at));
END $$;

DROP FUNCTION request_revision_processing(uuid,uuid,text);
CREATE FUNCTION request_revision_processing(p_actor uuid,p_revision uuid,p_key text,p_daily_limit integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE u users;r revisions;b buildings;s source_documents;receipt processing_command_receipts;j uuid;h text;lim integer;
 running uuid;used bigint;day_start timestamptz;retry integer;
BEGIN
 IF p_daily_limit IS NULL OR p_daily_limit NOT BETWEEN 1 AND 100000 THEN RAISE EXCEPTION 'IFC processing daily limit must be 1-100000'; END IF;
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO u FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF u.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF u.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 SELECT * INTO r FROM revisions WHERE id=p_revision;
 SELECT * INTO b FROM buildings WHERE id=r.building_id AND is_active AND deleted_at IS NULL;
 IF b.id IS NULL OR (u.role='OrganizationUser' AND u.organization_id IS DISTINCT FROM b.organization_id)
 OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('processing-command:'||p_actor||':'||p_key,0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 SELECT * INTO s FROM source_documents WHERE revision_id=r.id AND upload_verified_at IS NOT NULL AND source_etag IS NOT NULL;
 IF s.id IS NULL THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('revisionId',r.id,'sourceId',s.id,'sha256',s.sha256_hash));
 SELECT * INTO receipt FROM processing_command_receipts WHERE actor_id=p_actor AND operation='Process' AND idempotency_key=p_key;
 IF receipt.job_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  RETURN jsonb_build_object('code','OK','jobId',receipt.job_id);
 END IF;
 SELECT id INTO j FROM processing_jobs WHERE revision_id=r.id AND kind='Geometry' AND input_hash=lower(s.sha256_hash) AND scenario_version_id IS NULL;
 IF j IS NULL THEN
  -- #219: Organization limits for new Geometry jobs, serialized per Organization (taken after the building lock, never before it).
  -- A replayed key or an existing job of this revision returned above; requeue of a failed job (system failure) reuses its job and
  -- is never counted.
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ifc-processing:'||b.organization_id,0));
  -- Only a live job blocks: its Building is still active, and it is Queued for less than 2 hours or Running on a lease that has not
  -- expired (5 minutes of grace). A job stranded by an archived Building, a dead worker or a lost event never blocks the Organization.
  SELECT j2.id INTO running FROM processing_jobs j2 JOIN revisions r2 ON r2.id=j2.revision_id JOIN buildings b2 ON b2.id=r2.building_id
   LEFT JOIN processing_job_attempts pa2 ON pa2.id=j2.current_attempt_id
   WHERE b2.organization_id=b.organization_id AND b2.is_active AND b2.deleted_at IS NULL AND j2.kind='Geometry'
    -- Queued age counts from the latest queueing: a requeued job keeps its created_at but its last attempt just ended.
    AND ((j2.status='Queued' AND GREATEST(j2.created_at,COALESCE(pa2.finished_at,pa2.started_at,j2.created_at))>clock_timestamp()-interval '2 hours')
     OR (j2.status='Running' AND pa2.status='Running' AND pa2.lease_until>clock_timestamp()-interval '5 minutes'))
   ORDER BY j2.created_at,j2.id LIMIT 1;
  IF running IS NOT NULL THEN
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Reject','revisions',r.id,gen_random_uuid(),
     jsonb_build_object('operation','IfcProcessRejected','code','IFC_ORG_JOB_RUNNING','runningJobId',running),now());
   RETURN jsonb_build_object('code','IFC_ORG_JOB_RUNNING','status',409,'jobId',running,'errors',jsonb_build_object('jobId',jsonb_build_array(running::text)));
  END IF;
  day_start:=date_trunc('day',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
  SELECT count(*) INTO used FROM processing_jobs j2 JOIN revisions r2 ON r2.id=j2.revision_id JOIN buildings b2 ON b2.id=r2.building_id
   WHERE b2.organization_id=b.organization_id AND j2.kind='Geometry' AND j2.created_at>=day_start;
  -- The policy in effect (Organization, else default), else the configured starting value.
  lim:=(ifc_processing_effective_limit(b.organization_id,p_daily_limit)->>'dailyLimit')::int;
  -- A limit of 0 is PlatformAdmin stopping new processing for the Organization: no Retry-After, waiting never helps.
  IF lim=0 THEN
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Reject','revisions',r.id,gen_random_uuid(),
     jsonb_build_object('operation','IfcProcessRejected','code','IFC_PROCESSING_DISABLED'),now());
   RETURN jsonb_build_object('code','IFC_PROCESSING_DISABLED','status',403,'limit',0,'used',used);
  END IF;
  IF used>=lim THEN
   retry:=GREATEST(1,ceil(extract(epoch FROM (day_start+interval '1 day')-clock_timestamp()))::integer);
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
    VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Reject','revisions',r.id,gen_random_uuid(),
     jsonb_build_object('operation','IfcProcessRejected','code','IFC_PROCESS_DAILY_LIMIT','limit',lim,'used',used),now());
   RETURN jsonb_build_object('code','IFC_PROCESS_DAILY_LIMIT','status',429,'retryAfter',retry,
    'limit',lim,'used',used,'errors',jsonb_build_object('limit',jsonb_build_array(lim::text),'used',jsonb_build_array(used::text)));
  END IF;
  j:=gen_random_uuid();
  INSERT INTO processing_jobs(id,revision_id,source_document_id,kind,job_key,status,input_hash,created_at) VALUES(j,r.id,s.id,'Geometry',gen_random_uuid(),'Queued',lower(s.sha256_hash),now());
  PERFORM enqueue_integration_outbox_event('ifc-process:'||replace(j::text,'-',''),'ProcessingJob',j,'ProcessingJobRequested','1',jsonb_build_object('job_id',j,'revision_id',r.id,'source_key',s.storage_url,'input_hash',lower(s.sha256_hash)));
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','ProcessingJob',j,gen_random_uuid(),now());
 END IF;
 INSERT INTO processing_command_receipts(actor_id,operation,idempotency_key,input_hash,job_id) VALUES(p_actor,'Process',p_key,h,j);
 RETURN jsonb_build_object('code','OK','jobId',j);
END $$;

DO $grants$ DECLARE r text;s record;sig text;BEGIN
 ALTER FUNCTION request_revision_processing(uuid,uuid,text,integer) OWNER TO fet3d_ifc_upload_owner;
 ALTER TABLE ifc_processing_policies ENABLE ROW LEVEL SECURITY;
 REVOKE ALL ON ifc_processing_policies FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated','fire3d_api','fet3d_backend_executor'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON ifc_processing_policies FROM %I',r);END IF;END LOOP;
 GRANT SELECT,INSERT ON ifc_processing_policies TO fet3d_ifc_upload_owner;
 CREATE POLICY ifc_processing_policy_owner ON ifc_processing_policies TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON ifc_processing_policies TO fet3d_pending_cleanup_owner;
  CREATE POLICY pending_cleanup_owner ON ifc_processing_policies FOR SELECT TO fet3d_pending_cleanup_owner USING(true);
 END IF;
 GRANT INSERT ON audit_logs TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['ifc_processing_effective_limit(uuid,integer)','ifc_processing_usage(uuid,integer)','ifc_processing_policy_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','immutable_ifc_processing_policy()'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION ifc_processing_policy_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);END IF;END LOOP;
 FOREACH sig IN ARRAY ARRAY['request_revision_processing(uuid,uuid,text,integer)','ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text)'] LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION request_revision_processing(uuid,uuid,text,integer) TO %I',r);
 END IF; END LOOP;
 SELECT * INTO s FROM ifc_junk_guard_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
