-- BE#231: an AI scenario draft is a situation on a fire infrastructure (BE#213, BE#215, BE#224), and rag-api (AI#36, AI#39) reads it from bim.
-- * ai_org_gate (body of AiSourceRanking.sql, 20261028090000_AddAiSourceRanking): request.infrastructureId? pins an Approved version of the
--   Building (422 FIRE_INFRASTRUCTURE_REVISION_MISMATCH / FIRE_INFRASTRUCTURE_NOT_APPROVED as in CreateDraft); without revisionId it also pins
--   that version's revision. Otherwise the newest Approved version of the pinned revision is used. Its items (id, kind, category, label, anchorId,
--   capability, placement.floorId) go into bim.infrastructure {id, version, items}; the audit row names it. No Approved version: as before.
-- * ai_request_view (body of AiSourcesPreparing.sql, 20261031130000_AiSourcesPreparing): input.infrastructureId.
-- * ai_worker_gate (same body): a NeedsUserEdit draft whose request has bim.infrastructure and whose state still holds a Goal or fixed
--   Equipment (scenario_infrastructure_conflicts) is AI_RESULT_INVALID. The worker removes those objects first and says so in limitations.
-- Signatures are unchanged, so CREATE OR REPLACE keeps owner and grants. Requests already stored are unchanged.
CREATE TEMP TABLE ai_draft_infrastructure_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_draft_infrastructure_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;

CREATE OR REPLACE FUNCTION ai_org_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;pol ai_operation_policies;r ai_requests;receipt ai_command_receipts;g record;rv record;
 h text;pinned jsonb;requested jsonb;pinned_bim jsonb;building uuid;fi fire_infrastructures;remaining integer;take integer;available bigint;stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Sources','Limits','Submit','Get') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO org FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL;
 IF org.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;

 IF p_action='Sources' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',s.kind,'id',s.id,'versionId',s.version_id,
   'title',s.title,'versionLabel',s.version_label,'link',ai_source_link(s.kind,s.id)) ORDER BY s.kind,s.title,s.id) FROM ai_eligible_sources(org.id) s),'[]')));
 ELSIF p_action='Limits' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',(SELECT jsonb_agg(x.item ORDER BY x.op) FROM (
   SELECT o.op,CASE WHEN p.id IS NULL THEN jsonb_build_object('operation',o.op,'policyActive',false,'quotaUnit',NULL,'estimatedReserve',NULL,'maxInputChars',NULL,
     'maxSources',NULL,'availableUnits',0,'canSubmit',false)
    ELSE jsonb_build_object('operation',o.op,'policyActive',true,'quotaUnit',p.quota_unit,'estimatedReserve',p.reserve_units,'maxInputChars',p.max_input_chars,
     'maxSources',p.max_sources,'availableUnits',ai_org_available_units(org.id,p.quota_unit,stamp),'canSubmit',ai_org_available_units(org.id,p.quota_unit,stamp)>=p.reserve_units) END item
   FROM (VALUES('Answer'),('ScenarioDraft')) o(op)
   LEFT JOIN ai_operation_policies p ON p.operation=o.op AND p.effective_from<=stamp AND (p.effective_until IS NULL OR p.effective_until>stamp)) x)));
 ELSIF p_action='Get' THEN
  SELECT * INTO r FROM ai_requests WHERE id=p_resource AND organization_id=org.id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'organization'));
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
  RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'organization'),'replayed',true);
 END IF;
 IF p_input->>'operation' NOT IN('ScenarioDraft','Answer') OR jsonb_typeof(p_input->'request')<>'object' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 SELECT * INTO pol FROM ai_operation_policies WHERE operation=p_input->>'operation' AND effective_from<=stamp AND (effective_until IS NULL OR effective_until>stamp);
 IF pol.id IS NULL THEN RETURN jsonb_build_object('code','AI_POLICY_UNAVAILABLE','status',503);END IF;
 IF length(COALESCE(p_input->'request'->>'prompt',p_input->'request'->>'question',''))>pol.max_input_chars THEN RETURN jsonb_build_object('code','AI_INPUT_TOO_LARGE','status',422);END IF;
 IF p_input->>'operation'='ScenarioDraft' THEN
  SELECT b.id INTO building FROM buildings b WHERE b.id::text=p_input->'request'->>'buildingId' AND b.organization_id=org.id AND b.is_active AND b.deleted_at IS NULL;
  IF building IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  -- #231: a requested fire infrastructure is an Approved version of this Building on the pinned revision (the same codes as CreateDraft).
  IF NULLIF(p_input->'request'->>'infrastructureId','') IS NOT NULL THEN
   SELECT * INTO fi FROM fire_infrastructures WHERE id::text=p_input->'request'->>'infrastructureId' AND building_id=building;
   IF fi.id IS NULL OR (NULLIF(p_input->'request'->>'revisionId','') IS NOT NULL AND fi.revision_id::text<>lower(p_input->'request'->>'revisionId')) THEN
    RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_REVISION_MISMATCH','status',422,'errors',jsonb_build_object('infrastructureId',jsonb_build_array('Use a fire infrastructure version of the pinned revision.')));END IF;
   IF fi.status<>'Approved' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_APPROVED','status',422,'errors',jsonb_build_object('infrastructureId',jsonb_build_array('Only an Approved (locked) fire infrastructure version can carry scenarios.')));END IF;
  END IF;
  -- Pin one revision with accepted geometry: the requested one, the requested infrastructure's, or the newest revision of the Building that has it.
  IF NULLIF(p_input->'request'->>'revisionId','') IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM revisions v WHERE v.id::text=p_input->'request'->>'revisionId' AND v.building_id=building) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   pinned_bim:=ai_bim_snapshot((p_input->'request'->>'revisionId')::uuid);
  ELSIF fi.id IS NOT NULL THEN
   pinned_bim:=ai_bim_snapshot(fi.revision_id);
  ELSE
   FOR rv IN SELECT v.id FROM revisions v WHERE v.building_id=building AND v.status IN('ReadyForScenario','ConfirmedForTraining') ORDER BY v.created_at DESC,v.id LIMIT 20 LOOP
    pinned_bim:=ai_bim_snapshot(rv.id);EXIT WHEN pinned_bim IS NOT NULL;
   END LOOP;
  END IF;
  IF pinned_bim IS NULL THEN RETURN jsonb_build_object('code','AI_REVISION_NOT_READY','status',409);END IF;
  -- #231: the draft is a situation on the requested, else the newest Approved, fire infrastructure of the pinned revision. Its items go to the
  -- AI service in bim.infrastructure so the draft places no exit or fixed equipment of its own. No Approved version: a legacy request, as before.
  IF fi.id IS NULL THEN
   SELECT * INTO fi FROM fire_infrastructures WHERE revision_id=(pinned_bim->>'revisionId')::uuid AND status='Approved' ORDER BY version_number DESC LIMIT 1;
  END IF;
  IF fi.id IS NOT NULL THEN
   pinned_bim:=pinned_bim||jsonb_build_object('infrastructure',jsonb_build_object('id',fi.id,'version',fi.version_number,
    'items',COALESCE((SELECT jsonb_agg(jsonb_strip_nulls(jsonb_build_object('id',i->'id','kind',i->'kind','category',i->'category','label',i->'label',
      'anchorId',i->'anchorId','capability',i->'capability',
      'placement',CASE WHEN i->'placement'->'floorId' IS NOT NULL THEN jsonb_build_object('floorId',i->'placement'->'floorId') END)) ORDER BY n)
     FROM jsonb_array_elements(fi.items) WITH ORDINALITY x(i,n)),'[]')));
  END IF;
 END IF;
 -- Pin sources: the requested set must be eligible for this tenant now; an empty request pins the most relevant eligible sources up to the policy limit.
 requested:=COALESCE(p_input->'request'->'sources','[]');
 IF jsonb_array_length(requested)>pol.max_sources THEN RETURN jsonb_build_object('code','AI_TOO_MANY_SOURCES','status',422);END IF;
 IF jsonb_array_length(requested)>0 AND EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE NOT EXISTS(SELECT 1 FROM ai_eligible_sources(org.id) s WHERE s.kind=e->>'kind' AND s.id::text=e->>'id')) THEN
  RETURN jsonb_build_object('code','AI_SOURCE_NOT_ALLOWED','status',422);END IF;
 -- #178: most relevant to the prompt first; a scenario draft takes Library templates before documents.
 pinned:=ai_pick_sources(org.id,false,pol.operation,COALESCE(p_input->'request'->>'prompt',p_input->'request'->>'question'),requested,'[]',NULL,pol.max_sources);
 IF jsonb_array_length(pinned)=0 THEN RETURN jsonb_build_object('code','AI_NO_SOURCES','status',422);END IF;
 -- Reserve the policy amount across active grants of the pinned unit, earliest expiry first, under one organization lock.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-quota:'||org.id,0));
 available:=ai_org_available_units(org.id,pol.quota_unit,stamp);
 IF available<pol.reserve_units THEN RETURN jsonb_build_object('code','AI_QUOTA_EXHAUSTED','status',409);END IF;
 INSERT INTO ai_requests(id,organization_id,actor_user_id,audience,operation,policy_id,quota_unit,reserved_units,status,input,input_hash,sources,bim,next_attempt_at,created_at,updated_at)
 VALUES(gen_random_uuid(),org.id,p_actor,'organization',pol.operation,pol.id,pol.quota_unit,pol.reserve_units,'Accepted',p_input->'request',h,pinned,pinned_bim,stamp,stamp,stamp) RETURNING * INTO r;
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
 VALUES(gen_random_uuid(),p_actor,org.id,'User','Create','AiRequest',r.id,gen_random_uuid(),jsonb_build_object('operation',r.operation,'policyId',pol.id,'reservedUnits',r.reserved_units,'quotaUnit',r.quota_unit,'revisionId',pinned_bim->'revisionId','infrastructureId',pinned_bim->'infrastructure'->'id'),now());
 RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'organization'));
END $$;

CREATE OR REPLACE FUNCTION ai_request_view(p_id uuid,p_viewer text) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE r ai_requests;withheld boolean:=false;base jsonb;
BEGIN
 SELECT * INTO r FROM ai_requests WHERE id=p_id;
 IF r.id IS NULL THEN RETURN NULL;END IF;
 IF p_viewer='trainee' AND r.result IS NOT NULL THEN
  withheld:=CASE r.context->>'kind'
   WHEN 'Training' THEN ai_trainee_training_snapshot(r.actor_user_id,(r.context->>'trainingId')::uuid) IS NULL
   WHEN 'Learn' THEN NOT learn_rag_eligible((r.context->>'postId')::uuid,(r.context->>'versionId')::uuid)
   ELSE false END;
 END IF;
 base:=jsonb_build_object('requestId',r.id,'audience',r.audience,'operation',r.operation,'status',r.status,'resultType',r.result_type,
  'quotaUnit',r.quota_unit,'reservedUnits',r.reserved_units,'consumedUnits',r.consumed_units,
  'input',jsonb_build_object('text',COALESCE(r.input->>'question',r.input->>'prompt'),'inputHash',r.input_hash,
   'buildingId',r.input->'buildingId','buildingName',(SELECT to_jsonb(b.name) FROM buildings b WHERE b.id::text=r.input->>'buildingId'),
   'revisionId',r.bim->'revisionId','infrastructureId',r.bim->'infrastructure'->'id','requestedSources',COALESCE(r.input->'sources','[]'),'context',r.context-'snapshot'),
  'sources',COALESCE((SELECT jsonb_agg(s-'content' ORDER BY o) FROM jsonb_array_elements(r.sources) WITH ORDINALITY e(s,o)),'[]'),
  'result',CASE WHEN withheld OR r.result IS NULL THEN NULL ELSE r.result-'citations'-'ruleResults' END,
  'citations',CASE WHEN withheld THEN '[]'::jsonb ELSE COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',p->'kind','id',p->'id','versionId',p->'versionId',
    'title',p->'title','versionLabel',p->'versionLabel','chunkId',c->'chunkId','locator',c->'locator','relevance',c->'relevance','link',p->'link') ORDER BY o)
   FROM jsonb_array_elements(COALESCE(r.result->'citations','[]')) WITH ORDINALITY x(c,o)
   CROSS JOIN LATERAL (SELECT p FROM jsonb_array_elements(r.sources) p WHERE p->>'id'=c->>'documentId' AND p->>'versionId'=c->>'documentVersion' LIMIT 1) pp),'[]') END,
  'failureCode',r.failure_code,'resultWithheld',CASE WHEN p_viewer='trainee' THEN withheld END,
  'retryAfterSeconds',CASE WHEN r.failure_code='AI_SOURCES_PREPARING' THEN (r.provider_result->'refusal'->>'retryAfterSeconds')::int END,
  'createdAt',r.created_at,'completedAt',r.completed_at,'revision',r.revision);
 IF p_viewer<>'admin' THEN RETURN base;END IF;
 RETURN base||jsonb_build_object('organizationId',r.organization_id,'actorUserId',r.actor_user_id,'policyId',r.policy_id,'attempts',r.attempts,
  'modelVersion',r.model_version,'technicalUsage',r.technical_usage,'reconcileRequestedAt',r.reconcile_requested_at,'providerResult',r.provider_result,
  'updatedAt',r.updated_at,'bim',r.bim,
  'allocations',(SELECT jsonb_agg(jsonb_build_object('grantId',a.grant_id,'reservedUnits',a.reserved_units,'consumedUnits',a.consumed_units,'status',a.status) ORDER BY a.created_at,a.grant_id)
   FROM billing_ai_quota_allocations a WHERE a.request_id=r.id));
END $$;

CREATE OR REPLACE FUNCTION ai_worker_gate(p_action text,p_worker text,p_request uuid,p_lease uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE r ai_requests;a record;phase text;units integer;remaining integer;take integer;st text;bad text;rtype text;now_bim jsonb;
 lease_seconds integer:=COALESCE((p_input->>'leaseSeconds')::int,180);stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Claim','Complete','Retry','Unknown','Reject','NotFound') OR NULLIF(btrim(p_worker),'') IS NULL THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_action='Claim' THEN
  IF lease_seconds NOT BETWEEN 10 AND 3600 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- A crashed dispatch may have reached the provider: never re-send, hold the reservation for reconciliation.
  UPDATE ai_requests SET status='NeedsReconcile',failure_code='AI_OUTCOME_UNKNOWN',lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE status='Processing' AND lease_until<=stamp;
  UPDATE ai_requests SET lease_token=NULL,lease_until=NULL,reconcile_requested_at=COALESCE(reconcile_requested_at,stamp),updated_at=stamp WHERE status='NeedsReconcile' AND lease_until<=stamp;
  SELECT * INTO r FROM ai_requests WHERE (status='Accepted' AND next_attempt_at<=stamp) OR (status='NeedsReconcile' AND reconcile_requested_at IS NOT NULL AND lease_token IS NULL)
   ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','EMPTY');END IF;
  phase:=CASE WHEN r.status='Accepted' THEN 'Dispatch' ELSE 'Lookup' END;
  UPDATE ai_requests SET status=CASE WHEN phase='Dispatch' THEN 'Processing' ELSE status END,attempts=attempts+CASE WHEN phase='Dispatch' THEN 1 ELSE 0 END,
   lease_token=gen_random_uuid(),lease_until=stamp+make_interval(secs=>lease_seconds),reconcile_requested_at=NULL,updated_at=stamp WHERE id=r.id RETURNING * INTO r;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'phase',phase,'leaseToken',r.lease_token,'operation',r.operation,'audience',r.audience,
   'payload',jsonb_build_object('request_id',r.id,'audience',r.audience,'operation',r.operation,
    'response_type',CASE WHEN r.operation='ScenarioDraft' THEN 'ScenarioDraft' ELSE 'KnowledgeAnswer' END,
    'organization_id',r.organization_id,'input',jsonb_build_object('text',COALESCE(r.input->>'question',r.input->>'prompt')),'input_hash',r.input_hash,
    'sources',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',s->>'kind','document_id',s->'id','document_version',s->'versionId','title',s->'title',
      'version_label',s->'versionLabel','content_hash',s->'contentHash','source_uri',s->'link'->'sourceUri',
      'content',COALESCE(ai_source_content(s->>'kind',(s->>'versionId')::uuid),s->'content'),
      'content_omitted',s->>'kind' IN('LearnPost','LibraryVersion') AND ai_source_content(s->>'kind',(s->>'versionId')::uuid) IS NULL) ORDER BY o)
     FROM jsonb_array_elements(r.sources) WITH ORDINALITY e(s,o)),'[]'),
    'bim',r.bim,
    'learn_context',CASE WHEN r.context->>'kind'='Learn' THEN jsonb_build_object('post_id',r.context->'postId','version_id',r.context->'versionId') END,
    'context',r.context,'limits',jsonb_build_object('quota_unit',r.quota_unit,'max_units',r.reserved_units),'attempt',r.attempts)));
 END IF;

 SELECT * INTO r FROM ai_requests WHERE id=p_request FOR UPDATE;
 IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_lease IS NULL OR r.lease_token IS NULL OR r.lease_until IS NULL OR r.lease_token<>p_lease OR r.lease_until<=stamp OR r.status NOT IN('Processing','NeedsReconcile') THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
 phase:=CASE WHEN r.status='Processing' THEN 'Dispatch' ELSE 'Lookup' END;

 IF p_action='Complete' THEN
  st:=p_input->>'status';
  units:=CASE WHEN jsonb_typeof(p_input->'usage'->'units')='number' AND (p_input->'usage'->>'units') ~ '^[0-9]{1,9}$' THEN (p_input->'usage'->>'units')::int END;
  rtype:=CASE st WHEN 'Answered' THEN 'KnowledgeAnswer' WHEN 'NeedsUserEdit' THEN 'ScenarioDraft' WHEN 'InsufficientEvidence' THEN 'InsufficientEvidence'
   WHEN 'RejectedBySafetyGate' THEN 'RejectedBySafetyGate' END;
  IF r.operation='ScenarioDraft' AND st='NeedsUserEdit' THEN now_bim:=ai_bim_snapshot((r.bim->>'revisionId')::uuid);END IF;
  bad:=CASE
   WHEN p_input->>'requestId' IS DISTINCT FROM r.id::text OR rtype IS NULL OR jsonb_typeof(p_input->'result') IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_input->'citations') IS DISTINCT FROM 'array' OR jsonb_typeof(COALESCE(p_input->'bimAnchors','[]')) IS DISTINCT FROM 'array' THEN 'AI_RESULT_INVALID'
   -- NeedsUserEdit belongs to ScenarioDraft only, Answered to knowledge answers only.
   WHEN (st='NeedsUserEdit')<>(r.operation='ScenarioDraft' AND st='NeedsUserEdit') OR (st='Answered' AND r.operation='ScenarioDraft') THEN 'AI_RESULT_INVALID'
   WHEN p_input->>'responseType' IS DISTINCT FROM CASE WHEN r.operation='ScenarioDraft' THEN 'ScenarioDraft' ELSE 'KnowledgeAnswer' END THEN 'AI_RESULT_INVALID'
   WHEN units IS NULL THEN 'AI_USAGE_INVALID'
   WHEN units>r.reserved_units THEN 'AI_USAGE_EXCEEDS_RESERVATION'
   -- Citations name a pinned document version that is still eligible; the provider's own tenant filter is not trusted.
   WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'citations') c WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(r.sources) s
     WHERE s->>'id'=c->>'documentId' AND s->>'versionId'=c->>'documentVersion'
     AND (s->>'kind' IN('TrainingSnapshot','SessionResult')
      OR (r.audience='organization' AND EXISTS(SELECT 1 FROM ai_eligible_sources(r.organization_id) e WHERE e.kind=s->>'kind' AND e.id::text=s->>'id' AND e.version_id::text=s->>'versionId'))
      OR (r.audience='trainee' AND EXISTS(SELECT 1 FROM ai_trainee_eligible_sources() e WHERE e.kind=s->>'kind' AND e.id::text=s->>'id' AND e.version_id::text=s->>'versionId'))))) THEN 'AI_CITATION_INVALID'
   WHEN st IN('Answered','NeedsUserEdit') AND jsonb_array_length(p_input->'citations')=0 THEN 'AI_CITATION_INVALID'
   WHEN r.operation<>'ScenarioDraft' AND jsonb_array_length(COALESCE(p_input->'bimAnchors','[]'))>0 THEN 'AI_RESULT_INVALID'
   -- A draft must anchor to the pinned revision, keep its geometry pin, and still pass the editor reference check.
   WHEN st='NeedsUserEdit' AND (now_bim IS NULL OR now_bim->'geometry'->>'artifactId' IS DISTINCT FROM r.bim->'geometry'->>'artifactId') THEN 'AI_REVISION_STALE'
   WHEN st='NeedsUserEdit' AND (jsonb_array_length(COALESCE(p_input->'bimAnchors','[]'))=0
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'bimAnchors') b WHERE b->>'revisionId' IS DISTINCT FROM r.bim->>'revisionId'
      OR NOT (EXISTS(SELECT 1 FROM jsonb_array_elements(r.bim->'anchors') x WHERE x->>'ifcGlobalId'=b->>'ifcGlobalId')
       OR EXISTS(SELECT 1 FROM jsonb_array_elements(r.bim->'floors') f WHERE f->>'ifcGlobalId'=b->>'ifcGlobalId')))
     OR p_input->'result'->'draft'->'state'->'geometry'->>'artifactId' IS DISTINCT FROM r.bim->'geometry'->>'artifactId'
     OR p_input->'result'->'draft'->'state'->'geometry'->>'sha256Hash' IS DISTINCT FROM r.bim->'geometry'->>'sha256Hash'
     OR jsonb_array_length(scenario_reference_issues((r.bim->>'revisionId')::uuid,p_input->'result'->'draft'->'state'))>0) THEN 'AI_BIM_ANCHOR_INVALID'
   -- #231: a draft on a fire infrastructure places no exit or fixed equipment (the worker removes them before it settles).
   WHEN st='NeedsUserEdit' AND jsonb_typeof(r.bim->'infrastructure')='object'
     AND jsonb_array_length(scenario_infrastructure_conflicts(p_input->'result'->'draft'->'state'))>0 THEN 'AI_RESULT_INVALID'
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
  UPDATE ai_requests SET status='Succeeded',result_type=rtype,consumed_units=units,
   result=jsonb_build_object('status',st)||(p_input->'result')||jsonb_build_object('citations',p_input->'citations','bimAnchors',COALESCE(p_input->'bimAnchors','[]')),
   model_version=left(p_input->>'modelVersion',120),technical_usage=jsonb_strip_nulls(jsonb_build_object('inputTokens',p_input->'usage'->'inputTokens','outputTokens',p_input->'usage'->'outputTokens')),
   provider_result=NULL,failure_code=NULL,lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSIF p_action='Retry' THEN
  IF phase<>'Dispatch' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  IF r.attempts<LEAST(GREATEST(COALESCE((p_input->>'maxAttempts')::int,3),1),10) THEN
   UPDATE ai_requests SET status='Accepted',lease_token=NULL,lease_until=NULL,next_attempt_at=stamp+make_interval(secs=>LEAST(GREATEST(COALESCE((p_input->>'backoffSeconds')::int,30),1),3600)*r.attempts),
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
  -- The provider may name why it refused the pinned input; anything else stays the generic code. What it said (status, code, message, the
  -- document it could not use and, while sources are being prepared, how long to wait) is kept in provider_result for Admin, so a failed request can be traced to one source (BE#188).
  UPDATE ai_requests SET status='Failed',failure_code=CASE WHEN p_input->>'code' IN('AI_SOURCE_HASH_MISMATCH','AI_SOURCE_UNAVAILABLE','AI_REVISION_NOT_PINNED','AI_SOURCES_PREPARING')
   THEN p_input->>'code' ELSE 'AI_PROVIDER_REJECTED' END,
   provider_result=jsonb_build_object('refusal',jsonb_strip_nulls(jsonb_build_object('httpStatus',left(p_input->>'httpStatus',3),'code',left(p_input->>'code',80),
    'message',left(p_input->>'message',500),'documentId',left(p_input->>'documentId',64),
     'retryAfterSeconds',CASE WHEN p_input->>'retryAfterSeconds' ~ '^[0-9]{1,4}$' THEN (p_input->>'retryAfterSeconds')::int END))),lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSE -- NotFound: the provider has no record of the stable request id, so nothing was consumed.
  IF phase<>'Lookup' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  UPDATE ai_requests SET status='Failed',failure_code='AI_REQUEST_NOT_RECEIVED',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 END IF;
 SELECT * INTO r FROM ai_requests WHERE id=r.id;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status',r.status,'failureCode',r.failure_code));
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM ai_draft_infrastructure_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
