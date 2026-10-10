-- Organization AI requests and quota accounting on the existing billing ledger. Additive; nothing is seeded.
-- The ai_requests row is the durable dispatch record: the API commits request + reservation together and the
-- worker claims it with a lease, calls the FastAPI adapter outside any transaction and settles or releases atomically.
CREATE TEMP TABLE organization_ai_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO organization_ai_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Admin-configured, immutable reservation policy per operation. Units are whatever the quota grant unit is; no money/token ratio here.
CREATE TABLE ai_operation_policies(
 id uuid PRIMARY KEY,operation text NOT NULL CHECK(operation IN('ScenarioDraft','Answer')),
 quota_unit text NOT NULL CHECK(quota_unit ~ '^[a-z][a-z0-9_-]{0,49}$'),reserve_units integer NOT NULL CHECK(reserve_units BETWEEN 1 AND 100000000),
 max_input_chars integer NOT NULL CHECK(max_input_chars BETWEEN 1 AND 20000),max_sources integer NOT NULL CHECK(max_sources BETWEEN 1 AND 100),
 effective_from timestamptz NOT NULL,effective_until timestamptz,created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,
 CHECK(effective_until IS NULL OR effective_until>effective_from),
 EXCLUDE USING gist(operation WITH =,tstzrange(effective_from,effective_until,'[)') WITH &&));
CREATE FUNCTION immutable_ai_operation_policy() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'AI operation policies are immutable' USING ERRCODE='23514'; END $$;
CREATE TRIGGER ai_operation_policy_immutable BEFORE UPDATE OR DELETE ON ai_operation_policies FOR EACH ROW EXECUTE FUNCTION immutable_ai_operation_policy();

CREATE TABLE ai_requests(
 id uuid PRIMARY KEY,organization_id uuid NOT NULL REFERENCES organizations(id),actor_user_id uuid NOT NULL REFERENCES users(id),
 operation text NOT NULL CHECK(operation IN('ScenarioDraft','Answer')),policy_id uuid NOT NULL REFERENCES ai_operation_policies(id),
 quota_unit text NOT NULL,reserved_units integer NOT NULL CHECK(reserved_units>0),consumed_units integer CHECK(consumed_units>=0 AND consumed_units<=reserved_units),
 status text NOT NULL CHECK(status IN('Queued','Running','Succeeded','InsufficientEvidence','SafetyRejected','Failed','NeedsReconcile')),
 input jsonb NOT NULL CHECK(jsonb_typeof(input)='object'),input_hash varchar(64) NOT NULL,sources jsonb NOT NULL CHECK(jsonb_typeof(sources)='array'),
 result jsonb,provider_result jsonb,failure_code varchar(80),attempts integer NOT NULL DEFAULT 0 CHECK(attempts>=0),
 lease_token uuid,lease_until timestamptz,next_attempt_at timestamptz NOT NULL,reconcile_requested_at timestamptz,reconcile_requested_by uuid REFERENCES users(id),
 created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,completed_at timestamptz,revision bigint NOT NULL DEFAULT 1 CHECK(revision>0),
 CHECK((lease_token IS NULL)=(lease_until IS NULL)),
 CHECK(status<>'Running' OR lease_token IS NOT NULL),
 CHECK((status IN('Succeeded','InsufficientEvidence','SafetyRejected'))=(consumed_units IS NOT NULL AND result IS NOT NULL)),
 CHECK((status IN('Succeeded','InsufficientEvidence','SafetyRejected','Failed'))=(completed_at IS NOT NULL)));
CREATE INDEX ai_requests_dispatch ON ai_requests(status,next_attempt_at,created_at);
CREATE INDEX ai_requests_organization ON ai_requests(organization_id,created_at DESC,id);
CREATE TABLE ai_command_receipts(
 actor_user_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,input_hash varchar(64) NOT NULL,
 result jsonb NOT NULL,created_at timestamptz NOT NULL,PRIMARY KEY(actor_user_id,operation,idempotency_key));

-- Sources an organization may cite now: approved Common or own-tenant knowledge sources, published active Library versions and RAG-eligible Learn posts.
CREATE FUNCTION ai_eligible_sources(p_org uuid) RETURNS TABLE(kind text,id uuid,version_id uuid,title text,version_label text,content_hash text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT 'KnowledgeSource',k.id,k.id,k.title,k.version_label::text,k.source_hash::text FROM knowledge_sources k
  WHERE k.approval_status='Approved' AND (k.visibility='Common' OR k.organization_id=p_org)
   AND (k.effective_from IS NULL OR k.effective_from<=clock_timestamp()) AND (k.effective_until IS NULL OR k.effective_until>clock_timestamp())
 UNION ALL
 SELECT 'LibraryVersion',v.id,v.id,i.code||' '||v.name,'v'||v.version_number,v.payload_hash::text FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id
  WHERE v.status='Published' AND i.is_active
 UNION ALL
 SELECT 'LearnPost',p.id,v.id,v.title::text,'v'||v.version_number,v.content_hash::text FROM learn_posts p JOIN learn_post_versions v ON v.id=p.published_version_id
  WHERE learn_rag_eligible(p.id,v.id)
$$;

CREATE FUNCTION ai_request_representation(p_id uuid,p_admin boolean) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_strip_nulls(jsonb_build_object('requestId',r.id,'operation',r.operation,'status',r.status,'quotaUnit',r.quota_unit,'reservedUnits',r.reserved_units,
  'consumedUnits',r.consumed_units,'sources',r.sources,'result',r.result,'failureCode',r.failure_code,'createdAt',r.created_at,'completedAt',r.completed_at,'revision',r.revision))
  || CASE WHEN p_admin THEN jsonb_strip_nulls(jsonb_build_object('organizationId',r.organization_id,'actorUserId',r.actor_user_id,'policyId',r.policy_id,'attempts',r.attempts,
   'reconcileRequestedAt',r.reconcile_requested_at,'providerResult',r.provider_result,'updatedAt',r.updated_at,
   'allocations',(SELECT jsonb_agg(jsonb_build_object('grantId',a.grant_id,'reservedUnits',a.reserved_units,'consumedUnits',a.consumed_units,'status',a.status) ORDER BY a.created_at,a.grant_id)
    FROM billing_ai_quota_allocations a WHERE a.request_id=r.id))) ELSE '{}'::jsonb END
 FROM ai_requests r WHERE r.id=p_id
$$;

CREATE FUNCTION ai_release_request(p_id uuid,p_stamp timestamptz) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 UPDATE billing_ai_quota_allocations SET status='Released',settled_at=p_stamp WHERE request_id=p_id AND status='Reserved';
$$;

-- Organization user entry points: Sources, Submit, Get.
CREATE FUNCTION ai_org_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;pol ai_operation_policies;r ai_requests;receipt ai_command_receipts;g record;
 h text;pinned jsonb;requested jsonb;want integer;remaining integer;take integer;available bigint;stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Sources','Submit','Get') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO org FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL;
 IF org.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;

 IF p_action='Sources' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',s.kind,'id',s.id,'versionId',s.version_id,'title',s.title,'versionLabel',s.version_label)
   ORDER BY s.kind,s.title,s.id) FROM ai_eligible_sources(org.id) s),'[]')));
 ELSIF p_action='Get' THEN
  SELECT * INTO r FROM ai_requests WHERE id=p_resource AND organization_id=org.id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_representation(r.id,false));
 END IF;

 -- Submit
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 h:=fet3d_jsonb_payload_hash(p_input);
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-receipt:'||p_actor||':'||p_key,0));
 SELECT * INTO receipt FROM ai_command_receipts WHERE actor_user_id=p_actor AND operation='AiSubmit' AND idempotency_key=p_key;
 IF receipt.actor_user_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  SELECT * INTO r FROM ai_requests WHERE id=(receipt.result->>'requestId')::uuid AND organization_id=org.id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_representation(r.id,false),'replayed',true);
 END IF;
 IF p_input->>'operation' NOT IN('ScenarioDraft','Answer') OR jsonb_typeof(p_input->'request')<>'object' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 SELECT * INTO pol FROM ai_operation_policies WHERE operation=p_input->>'operation' AND effective_from<=stamp AND (effective_until IS NULL OR effective_until>stamp);
 IF pol.id IS NULL THEN RETURN jsonb_build_object('code','AI_POLICY_UNAVAILABLE','status',503);END IF;
 IF length(COALESCE(p_input->'request'->>'prompt',p_input->'request'->>'question',''))>pol.max_input_chars THEN RETURN jsonb_build_object('code','AI_INPUT_TOO_LARGE','status',422);END IF;
 IF p_input->>'operation'='ScenarioDraft' AND NOT EXISTS(SELECT 1 FROM buildings b WHERE b.id=(p_input->'request'->>'buildingId')::uuid AND b.organization_id=org.id AND b.is_active AND b.deleted_at IS NULL) THEN
  RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- Pin sources: the requested set must be eligible for this tenant now; an empty request pins every eligible source up to the policy limit.
 requested:=COALESCE(p_input->'request'->'sources','[]');
 IF jsonb_array_length(requested)>pol.max_sources THEN RETURN jsonb_build_object('code','AI_TOO_MANY_SOURCES','status',422);END IF;
 IF jsonb_array_length(requested)=0 THEN
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.kind,x.title,x.id),'[]') INTO pinned FROM (SELECT s.kind,s.title,s.id,jsonb_build_object('kind',s.kind,'id',s.id,'versionId',s.version_id,'title',s.title,'versionLabel',s.version_label,'contentHash',s.content_hash) item
   FROM ai_eligible_sources(org.id) s ORDER BY s.kind,s.title,s.id LIMIT pol.max_sources) x;
 ELSE
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE NOT EXISTS(SELECT 1 FROM ai_eligible_sources(org.id) s WHERE s.kind=e->>'kind' AND s.id::text=e->>'id')) THEN
   RETURN jsonb_build_object('code','AI_SOURCE_NOT_ALLOWED','status',422);END IF;
  SELECT jsonb_agg(jsonb_build_object('kind',s.kind,'id',s.id,'versionId',s.version_id,'title',s.title,'versionLabel',s.version_label,'contentHash',s.content_hash) ORDER BY s.kind,s.title,s.id) INTO pinned
   FROM ai_eligible_sources(org.id) s WHERE EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE s.kind=e->>'kind' AND s.id::text=e->>'id');
 END IF;
 IF jsonb_array_length(pinned)=0 THEN RETURN jsonb_build_object('code','AI_NO_SOURCES','status',422);END IF;
 -- Reserve the policy amount across active grants of the pinned unit, earliest expiry first, under one organization lock.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-quota:'||org.id,0));
 SELECT COALESCE(sum(greatest(gr.quota_units-u.used,0)),0) INTO available FROM billing_ai_quota_grants gr
  CROSS JOIN LATERAL (SELECT COALESCE(sum(CASE a.status WHEN 'Reserved' THEN a.reserved_units WHEN 'Settled' THEN a.consumed_units ELSE 0 END),0) used FROM billing_ai_quota_allocations a WHERE a.grant_id=gr.id) u
  WHERE gr.organization_id=org.id AND gr.quota_unit=pol.quota_unit AND gr.starts_at<=stamp AND gr.ends_at>stamp;
 IF available<pol.reserve_units THEN RETURN jsonb_build_object('code','AI_QUOTA_EXHAUSTED','status',409);END IF;
 INSERT INTO ai_requests(id,organization_id,actor_user_id,operation,policy_id,quota_unit,reserved_units,status,input,input_hash,sources,next_attempt_at,created_at,updated_at)
 VALUES(gen_random_uuid(),org.id,p_actor,pol.operation,pol.id,pol.quota_unit,pol.reserve_units,'Queued',p_input->'request',h,pinned,stamp,stamp,stamp) RETURNING * INTO r;
 remaining:=pol.reserve_units;
 FOR g IN SELECT gr.id,greatest(gr.quota_units-u.used,0) free FROM billing_ai_quota_grants gr
  CROSS JOIN LATERAL (SELECT COALESCE(sum(CASE a.status WHEN 'Reserved' THEN a.reserved_units WHEN 'Settled' THEN a.consumed_units ELSE 0 END),0) used FROM billing_ai_quota_allocations a WHERE a.grant_id=gr.id) u
  WHERE gr.organization_id=org.id AND gr.quota_unit=pol.quota_unit AND gr.starts_at<=stamp AND gr.ends_at>stamp ORDER BY gr.ends_at,gr.created_at,gr.id LOOP
  EXIT WHEN remaining=0;
  CONTINUE WHEN g.free<=0;
  take:=least(remaining,g.free);
  INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,status,created_at) VALUES(g.id,org.id,r.id,pol.quota_unit,take,'Reserved',stamp);
  remaining:=remaining-take;
 END LOOP;
 INSERT INTO ai_command_receipts VALUES(p_actor,'AiSubmit',p_key,h,jsonb_build_object('requestId',r.id),stamp);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,org.id,'User','Create','AiRequest',r.id,gen_random_uuid(),jsonb_build_object('operation',r.operation,'policyId',pol.id,'reservedUnits',r.reserved_units,'quotaUnit',r.quota_unit),now());
 RETURN jsonb_build_object('code','OK','result',ai_request_representation(r.id,false));
END $$;

-- Worker: Claim, Complete (validated result), Retry (provably not delivered), Unknown (outcome unknown), Reject (provider refused), NotFound (lookup: never received).
CREATE FUNCTION ai_worker_gate(p_action text,p_worker text,p_request uuid,p_lease uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE r ai_requests;a record;phase text;units integer;remaining integer;take integer;outcome text;bad text;
 lease_seconds integer:=COALESCE((p_input->>'leaseSeconds')::int,180);stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Claim','Complete','Retry','Unknown','Reject','NotFound') OR NULLIF(btrim(p_worker),'') IS NULL THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_action='Claim' THEN
  IF lease_seconds NOT BETWEEN 10 AND 3600 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- A crashed dispatch may have reached the provider: never re-send, hold the reservation for reconciliation.
  UPDATE ai_requests SET status='NeedsReconcile',failure_code='AI_OUTCOME_UNKNOWN',lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE status='Running' AND lease_until<=stamp;
  UPDATE ai_requests SET lease_token=NULL,lease_until=NULL,reconcile_requested_at=COALESCE(reconcile_requested_at,stamp),updated_at=stamp WHERE status='NeedsReconcile' AND lease_until<=stamp;
  SELECT * INTO r FROM ai_requests WHERE (status='Queued' AND next_attempt_at<=stamp) OR (status='NeedsReconcile' AND reconcile_requested_at IS NOT NULL AND lease_token IS NULL)
   ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','EMPTY');END IF;
  phase:=CASE WHEN r.status='Queued' THEN 'Dispatch' ELSE 'Lookup' END;
  UPDATE ai_requests SET status=CASE WHEN phase='Dispatch' THEN 'Running' ELSE status END,attempts=attempts+CASE WHEN phase='Dispatch' THEN 1 ELSE 0 END,
   lease_token=gen_random_uuid(),lease_until=stamp+make_interval(secs=>lease_seconds),reconcile_requested_at=NULL,updated_at=stamp WHERE id=r.id RETURNING * INTO r;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'phase',phase,'leaseToken',r.lease_token,'operation',r.operation,'organizationId',r.organization_id,
   'request',r.input,'sources',r.sources,'limits',jsonb_build_object('quotaUnit',r.quota_unit,'maxUnits',r.reserved_units),'attempt',r.attempts,
   'building',CASE WHEN r.operation='ScenarioDraft' THEN (SELECT jsonb_build_object('id',b.id,'name',b.name) FROM buildings b WHERE b.id=(r.input->>'buildingId')::uuid AND b.organization_id=r.organization_id) END));
 END IF;

 SELECT * INTO r FROM ai_requests WHERE id=p_request FOR UPDATE;
 IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF r.lease_token IS DISTINCT FROM p_lease OR r.lease_until<=stamp OR r.status NOT IN('Running','NeedsReconcile') THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
 phase:=CASE WHEN r.status='Running' THEN 'Dispatch' ELSE 'Lookup' END;

 IF p_action='Complete' THEN
  outcome:=p_input->>'outcome';units:=CASE WHEN jsonb_typeof(p_input->'usage'->'units')='number' AND (p_input->'usage'->>'units') ~ '^[0-9]{1,9}$' THEN (p_input->'usage'->>'units')::int END;
  bad:=CASE
   WHEN p_input->>'requestId' IS DISTINCT FROM r.id::text THEN 'AI_RESULT_INVALID'
   WHEN outcome IS NULL OR outcome NOT IN('Succeeded','InsufficientEvidence','SafetyRejected') OR jsonb_typeof(p_input->'result') IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_input->'citations') IS DISTINCT FROM 'array' THEN 'AI_RESULT_INVALID'
   WHEN p_input->'usage'->>'quotaUnit' IS DISTINCT FROM r.quota_unit OR units IS NULL THEN 'AI_USAGE_INVALID'
   WHEN units>r.reserved_units THEN 'AI_USAGE_EXCEEDS_RESERVATION'
   -- Citations are re-checked here: pinned for this request and still eligible for this tenant; the provider's own tenant filter is not trusted.
   WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'citations') c WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(r.sources) s WHERE s->>'kind'=c->>'kind' AND s->>'id'=c->>'id')
     OR NOT EXISTS(SELECT 1 FROM ai_eligible_sources(r.organization_id) e WHERE e.kind=c->>'kind' AND e.id::text=c->>'id')) THEN 'AI_CITATION_INVALID'
   WHEN outcome='Succeeded' AND jsonb_array_length(p_input->'citations')=0 THEN 'AI_CITATION_INVALID'
  END;
  IF bad IS NOT NULL THEN
   UPDATE ai_requests SET status='NeedsReconcile',failure_code=bad,provider_result=p_input,lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE id=r.id;
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status','NeedsReconcile','failureCode',bad));
  END IF;
  remaining:=units;
  FOR a IN SELECT al.id,al.reserved_units FROM billing_ai_quota_allocations al JOIN billing_ai_quota_grants gr ON gr.id=al.grant_id
   WHERE al.request_id=r.id AND al.status='Reserved' ORDER BY gr.ends_at,gr.created_at,gr.id FOR UPDATE OF al LOOP
   take:=least(remaining,a.reserved_units);
   IF take>0 THEN UPDATE billing_ai_quota_allocations SET status='Settled',consumed_units=take,settled_at=stamp WHERE id=a.id;
   ELSE UPDATE billing_ai_quota_allocations SET status='Released',settled_at=stamp WHERE id=a.id;END IF;
   remaining:=remaining-take;
  END LOOP;
  UPDATE ai_requests SET status=outcome,consumed_units=units,result=jsonb_build_object('outcome',outcome,'citations',p_input->'citations')||(p_input->'result'),
   provider_result=NULL,failure_code=NULL,lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSIF p_action='Retry' THEN
  IF phase<>'Dispatch' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  IF r.attempts<LEAST(GREATEST(COALESCE((p_input->>'maxAttempts')::int,3),1),10) THEN
   UPDATE ai_requests SET status='Queued',lease_token=NULL,lease_until=NULL,next_attempt_at=stamp+make_interval(secs=>LEAST(GREATEST(COALESCE((p_input->>'backoffSeconds')::int,30),1),3600)*r.attempts),
    failure_code=left(p_input->>'reason',80),updated_at=stamp,revision=revision+1 WHERE id=r.id;
  ELSE
   PERFORM ai_release_request(r.id,stamp);
   UPDATE ai_requests SET status='Failed',failure_code='AI_PROVIDER_UNREACHABLE',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
  END IF;
 ELSIF p_action='Unknown' THEN
  UPDATE ai_requests SET status='NeedsReconcile',failure_code='AI_OUTCOME_UNKNOWN',lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSIF p_action='Reject' THEN
  IF phase<>'Dispatch' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  UPDATE ai_requests SET status='Failed',failure_code='AI_PROVIDER_REJECTED',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSE -- NotFound: the provider has no record of the stable request id, so nothing was consumed.
  IF phase<>'Lookup' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  UPDATE ai_requests SET status='Failed',failure_code='AI_REQUEST_NOT_RECEIVED',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 END IF;
 SELECT * INTO r FROM ai_requests WHERE id=r.id;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status',r.status,'failureCode',r.failure_code));
END $$;

-- Platform Admin: AI policies, knowledge source registry, request list/detail and reconciliation scheduling.
CREATE FUNCTION ai_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;r ai_requests;k knowledge_sources;pol ai_operation_policies;receipt ai_command_receipts;h text;result jsonb;total integer;
 page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('ListRequests','GetRequest','Reconcile','ListPolicies','CreatePolicy','ListSources','GetSource','CreateSource','ApproveSource','RetireSource') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;

 IF p_action='ListRequests' THEN
  SELECT count(*) INTO total FROM ai_requests WHERE (p_input->>'status' IS NULL OR status=p_input->>'status') AND (p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid);
  SELECT COALESCE(jsonb_agg(ai_request_representation(x.id,true) ORDER BY x.created_at DESC,x.id),'[]') INTO result FROM (SELECT id,created_at FROM ai_requests
   WHERE (p_input->>'status' IS NULL OR status=p_input->>'status') AND (p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid)
   ORDER BY created_at DESC,id OFFSET (page-1)*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='GetRequest' THEN
  IF NOT EXISTS(SELECT 1 FROM ai_requests WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_representation(p_resource,true));
 ELSIF p_action='ListPolicies' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',id,'operation',operation,'quotaUnit',quota_unit,'reserveUnits',reserve_units,
   'maxInputChars',max_input_chars,'maxSources',max_sources,'effectiveFrom',effective_from,'effectiveUntil',effective_until,'createdAt',created_at) ORDER BY operation,effective_from DESC,id) FROM ai_operation_policies),'[]')));
 ELSIF p_action='ListSources' THEN
  SELECT count(*) INTO total FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status';
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.created_at DESC,x.id),'[]') INTO result FROM (SELECT id,created_at,jsonb_build_object('id',id,'visibility',visibility,'organizationId',organization_id,'title',title,
   'sourceUri',source_uri,'versionLabel',version_label,'sourceHash',source_hash,'jurisdiction',jurisdiction,'effectiveFrom',effective_from,'effectiveUntil',effective_until,'approvalStatus',approval_status,
   'approvedAt',approved_at,'createdAt',created_at,'revision',revision) item FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status'
   ORDER BY created_at DESC,id OFFSET (page-1)*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 END IF;

 IF p_action IN('CreatePolicy','CreateSource','Reconcile') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-receipt:'||p_actor||':'||p_key,0));
  SELECT * INTO receipt FROM ai_command_receipts WHERE actor_user_id=p_actor AND operation='Admin'||p_action AND idempotency_key=p_key;
  IF receipt.actor_user_id IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result,'replayed',true);
  END IF;
 ELSIF p_action IN('ApproveSource','RetireSource') AND p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);
 END IF;

 IF p_action='Reconcile' THEN
  -- Scheduling only: the worker looks the stable request id up at the provider; consumed numbers are never edited here.
  SELECT * INTO r FROM ai_requests WHERE id=p_resource FOR UPDATE;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF r.status<>'NeedsReconcile' THEN RETURN jsonb_build_object('code','AI_RECONCILE_NOT_ALLOWED','status',409);END IF;
  UPDATE ai_requests SET reconcile_requested_at=COALESCE(reconcile_requested_at,stamp),reconcile_requested_by=p_actor,updated_at=stamp,revision=revision+1 WHERE id=r.id;
  result:=ai_request_representation(r.id,true);
 ELSIF p_action='CreatePolicy' THEN
  BEGIN
   INSERT INTO ai_operation_policies(id,operation,quota_unit,reserve_units,max_input_chars,max_sources,effective_from,effective_until,created_by,created_at)
   VALUES(gen_random_uuid(),p_input->>'operation',p_input->>'quotaUnit',(p_input->>'reserveUnits')::int,(p_input->>'maxInputChars')::int,(p_input->>'maxSources')::int,
    COALESCE((p_input->>'effectiveFrom')::timestamptz,stamp),(p_input->>'effectiveUntil')::timestamptz,p_actor,stamp) RETURNING * INTO pol;
  EXCEPTION WHEN exclusion_violation THEN RETURN jsonb_build_object('code','AI_POLICY_OVERLAP','status',409);
   WHEN check_violation OR not_null_violation OR invalid_text_representation OR datetime_field_overflow OR numeric_value_out_of_range THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
  END;
  result:=jsonb_build_object('id',pol.id,'operation',pol.operation,'quotaUnit',pol.quota_unit,'reserveUnits',pol.reserve_units,'maxInputChars',pol.max_input_chars,'maxSources',pol.max_sources,
   'effectiveFrom',pol.effective_from,'effectiveUntil',pol.effective_until,'createdAt',pol.created_at);
 ELSIF p_action='CreateSource' THEN
  IF p_input->>'visibility'='Organization' AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=(p_input->>'organizationId')::uuid AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  BEGIN
   INSERT INTO knowledge_sources(id,organization_id,visibility,title,source_uri,version_label,source_hash,jurisdiction,effective_from,effective_until,approval_status,created_by,created_at)
   VALUES(gen_random_uuid(),CASE WHEN p_input->>'visibility'='Organization' THEN (p_input->>'organizationId')::uuid END,p_input->>'visibility',btrim(p_input->>'title'),p_input->>'sourceUri',
    p_input->>'versionLabel',lower(p_input->>'sourceHash'),p_input->>'jurisdiction',(p_input->>'effectiveFrom')::timestamptz,(p_input->>'effectiveUntil')::timestamptz,'Draft',p_actor,stamp) RETURNING * INTO k;
  EXCEPTION WHEN unique_violation THEN RETURN jsonb_build_object('code','KNOWLEDGE_SOURCE_EXISTS','status',409);
   WHEN check_violation OR not_null_violation OR invalid_text_representation OR datetime_field_overflow OR string_data_right_truncation THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
  END;
  result:=jsonb_build_object('id',k.id,'visibility',k.visibility,'organizationId',k.organization_id,'title',k.title,'sourceUri',k.source_uri,'versionLabel',k.version_label,'sourceHash',k.source_hash,
   'jurisdiction',k.jurisdiction,'effectiveFrom',k.effective_from,'effectiveUntil',k.effective_until,'approvalStatus',k.approval_status,'createdAt',k.created_at,'revision',k.revision);
 ELSE -- GetSource, ApproveSource, RetireSource
  SELECT * INTO k FROM knowledge_sources WHERE id=p_resource FOR UPDATE;
  IF k.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p_action<>'GetSource' THEN
   IF k.revision<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
   IF p_action='ApproveSource' AND k.approval_status<>'Draft' OR p_action='RetireSource' AND k.approval_status<>'Approved' THEN RETURN jsonb_build_object('code','KNOWLEDGE_SOURCE_STATE_CONFLICT','status',409);END IF;
   UPDATE knowledge_sources SET approval_status=CASE WHEN p_action='ApproveSource' THEN 'Approved' ELSE 'Retired' END,
    approved_by=CASE WHEN p_action='ApproveSource' THEN p_actor ELSE approved_by END,approved_at=CASE WHEN p_action='ApproveSource' THEN stamp ELSE approved_at END,revision=revision+1
    WHERE id=k.id RETURNING * INTO k;
   -- Learn citations and AI eligibility read the live state, so retiring a source takes effect without touching published versions.
   PERFORM content_invalidate('knowledge',k.id,k.revision,jsonb_build_object('approvalStatus',k.approval_status));
  END IF;
  result:=jsonb_build_object('id',k.id,'visibility',k.visibility,'organizationId',k.organization_id,'title',k.title,'sourceUri',k.source_uri,'versionLabel',k.version_label,'sourceHash',k.source_hash,
   'jurisdiction',k.jurisdiction,'effectiveFrom',k.effective_from,'effectiveUntil',k.effective_until,'approvalStatus',k.approval_status,'approvedAt',k.approved_at,'createdAt',k.created_at,'revision',k.revision);
  IF p_action='GetSource' THEN RETURN jsonb_build_object('code','OK','result',result);END IF;
 END IF;
 IF p_action IN('CreatePolicy','CreateSource','Reconcile') THEN INSERT INTO ai_command_receipts VALUES(p_actor,'Admin'||p_action,p_key,h,result,stamp);END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User',(CASE WHEN p_action LIKE 'Create%' THEN 'Create' ELSE 'Update' END)::audit_action_enum,CASE WHEN p_action='Reconcile' THEN 'AiRequest' WHEN p_action='CreatePolicy' THEN 'AiOperationPolicy' ELSE 'KnowledgeSource' END,
  (result->>CASE WHEN p_action='Reconcile' THEN 'requestId' ELSE 'id' END)::uuid,gen_random_uuid(),jsonb_build_object('operation',p_action),now());
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

-- Privileges: tables are written only by the gates; the gate owner reads grants and writes allocations under its own policy.
DO $grants$ DECLARE t text;r text;sig text;s record;BEGIN
 FOREACH t IN ARRAY ARRAY['ai_operation_policies','ai_requests','ai_command_receipts'] LOOP
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY ai_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON %I FROM %I',t,r);END IF;END LOOP;
  FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON %I FROM %I',t,r);END IF;END LOOP;
 END LOOP;
 GRANT SELECT,INSERT ON ai_operation_policies,ai_command_receipts TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT,UPDATE ON ai_requests TO fet3d_ifc_upload_owner;
 CREATE POLICY ai_gate_owner_read ON billing_ai_quota_grants FOR SELECT TO fet3d_ifc_upload_owner USING(true);
 CREATE POLICY ai_gate_owner ON billing_ai_quota_allocations TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
 GRANT SELECT ON billing_ai_quota_grants TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT,UPDATE ON billing_ai_quota_allocations TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['ai_eligible_sources(uuid)','ai_request_representation(uuid,boolean)','ai_release_request(uuid,timestamp with time zone)',
   'ai_org_gate(text,uuid,uuid,uuid,jsonb,text)','ai_worker_gate(text,text,uuid,uuid,jsonb)','ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION ai_org_gate(text,uuid,uuid,uuid,jsonb,text),ai_worker_gate(text,text,uuid,uuid,jsonb),ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
 END IF;END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  FOREACH t IN ARRAY ARRAY['ai_operation_policies','ai_requests','ai_command_receipts'] LOOP
   EXECUTE format('GRANT SELECT ON %I TO fet3d_pending_cleanup_owner',t);
   EXECUTE format('CREATE POLICY pending_cleanup_owner ON %I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)',t);
  END LOOP;
 END IF;
 SELECT * INTO s FROM organization_ai_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
