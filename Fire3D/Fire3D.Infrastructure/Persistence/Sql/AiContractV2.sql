-- AI contract v2 (#89, #91, #77). Docs v7 vocabulary for requests (status Accepted|Processing|Succeeded|Failed|NeedsReconcile,
-- result_type KnowledgeAnswer|ScenarioDraft|InsufficientEvidence|RejectedBySafetyGate), pinned BIM facts for scenario drafts,
-- linkable citations, source content in the dispatch, and Trainee answers with an Admin-configured daily quota.
-- Existing rows are mapped to the new vocabulary in place; nothing is deleted.
CREATE TEMP TABLE ai_contract_v2_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_contract_v2_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- 1. Request vocabulary, audience and pinned context.
DO $$ DECLARE c record;BEGIN
 FOR c IN SELECT conname FROM pg_constraint WHERE conrelid='ai_requests'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) ~ '(status|operation)' LOOP
  EXECUTE format('ALTER TABLE ai_requests DROP CONSTRAINT %I',c.conname);
 END LOOP;
 FOR c IN SELECT conname FROM pg_constraint WHERE conrelid='ai_operation_policies'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) ~ 'operation' LOOP
  EXECUTE format('ALTER TABLE ai_operation_policies DROP CONSTRAINT %I',c.conname);
 END LOOP;
END $$;
ALTER TABLE ai_requests ALTER COLUMN organization_id DROP NOT NULL,
 ADD COLUMN audience text NOT NULL DEFAULT 'organization',ADD COLUMN result_type text,ADD COLUMN context jsonb,ADD COLUMN bim jsonb,
 ADD COLUMN model_version text,ADD COLUMN technical_usage jsonb;
UPDATE ai_requests SET
 result_type=CASE status WHEN 'Succeeded' THEN CASE operation WHEN 'ScenarioDraft' THEN 'ScenarioDraft' ELSE 'KnowledgeAnswer' END
  WHEN 'InsufficientEvidence' THEN 'InsufficientEvidence' WHEN 'SafetyRejected' THEN 'RejectedBySafetyGate' END,
 result=CASE WHEN result IS NULL THEN NULL ELSE (result-'outcome')||jsonb_build_object('status',CASE status
  WHEN 'Succeeded' THEN CASE operation WHEN 'ScenarioDraft' THEN 'NeedsUserEdit' ELSE 'Answered' END
  WHEN 'SafetyRejected' THEN 'RejectedBySafetyGate' ELSE status END) END,
 status=CASE status WHEN 'Queued' THEN 'Accepted' WHEN 'Running' THEN 'Processing' WHEN 'InsufficientEvidence' THEN 'Succeeded'
  WHEN 'SafetyRejected' THEN 'Succeeded' ELSE status END;
ALTER TABLE ai_requests
 ADD CONSTRAINT ai_requests_operation_check CHECK(operation IN('ScenarioDraft','Answer','TraineeAnswer')),
 ADD CONSTRAINT ai_requests_status_check CHECK(status IN('Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected')),
 ADD CONSTRAINT ai_requests_audience_check CHECK(audience IN('organization','trainee')
  AND (audience='organization')=(organization_id IS NOT NULL) AND (audience='trainee')=(operation='TraineeAnswer')),
 ADD CONSTRAINT ai_requests_result_type_check CHECK(result_type IS NULL OR result_type IN('KnowledgeAnswer','ScenarioDraft','InsufficientEvidence','RejectedBySafetyGate')),
 ADD CONSTRAINT ai_requests_processing_lease CHECK(status<>'Processing' OR lease_token IS NOT NULL),
 ADD CONSTRAINT ai_requests_succeeded_settled CHECK((status='Succeeded')=(consumed_units IS NOT NULL AND result IS NOT NULL AND result_type IS NOT NULL)),
 ADD CONSTRAINT ai_requests_terminal_completed CHECK((status IN('Succeeded','Failed','Rejected'))=(completed_at IS NOT NULL));
CREATE INDEX ai_requests_trainee ON ai_requests(actor_user_id,created_at DESC,id) WHERE audience='trainee';

-- TraineeAnswer policies carry the per-Trainee daily allowance (units per Vietnam calendar day).
ALTER TABLE ai_operation_policies ADD COLUMN daily_units integer,
 ADD CONSTRAINT ai_operation_policies_operation_check CHECK(operation IN('ScenarioDraft','Answer','TraineeAnswer')),
 ADD CONSTRAINT ai_operation_policies_daily_units CHECK((operation='TraineeAnswer')=(daily_units IS NOT NULL)
  AND (daily_units IS NULL OR (daily_units BETWEEN 1 AND 100000000 AND daily_units>=reserve_units)));

-- 2. Sources: links, content for the dispatch, Trainee eligibility.
CREATE FUNCTION ai_source_link(p_kind text,p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('learnSlug',(SELECT p.slug FROM learn_posts p WHERE p_kind='LearnPost' AND p.id=p_id),
  'learnPostId',CASE WHEN p_kind='LearnPost' THEN p_id END,
  'libraryItemId',(SELECT v.item_id FROM organization_library_versions v WHERE p_kind='LibraryVersion' AND v.id=p_id),
  'sourceUri',(SELECT k.source_uri FROM knowledge_sources k WHERE p_kind='KnowledgeSource' AND k.id=p_id))
$$;

CREATE FUNCTION ai_source_pin(p_kind text,p_id uuid,p_version uuid,p_title text,p_label text,p_hash text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('kind',p_kind,'id',p_id,'versionId',p_version,'title',p_title,'versionLabel',p_label,'contentHash',p_hash,'link',ai_source_link(p_kind,p_id))
$$;

-- Learn and Library content travels in the dispatch (at most 256 KiB per source). Knowledge source documents are fetched by
-- the AI service from sourceUri and must match contentHash (SHA-256), or read from knowledge_chunks of that source id.
CREATE FUNCTION ai_source_content(p_kind text,p_version uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT CASE WHEN octet_length(c::text)>262144 THEN NULL ELSE c END FROM (
  SELECT jsonb_build_object('title',v.title,'summary',v.summary,'contentKind',v.content_kind,'blocks',v.content_blocks) c
   FROM learn_post_versions v WHERE p_kind='LearnPost' AND v.id=p_version
  UNION ALL
  SELECT jsonb_build_object('kind',i.kind,'code',i.code,'name',v.name,'payload',v.payload,'requiredCapabilities',v.required_capabilities)
   FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id WHERE p_kind='LibraryVersion' AND v.id=p_version) x
$$;

-- Trainees may use approved Common knowledge sources and RAG-eligible Learn posts (Published or Hidden), never Library rubrics
-- or Organization sources.
CREATE FUNCTION ai_trainee_eligible_sources() RETURNS TABLE(kind text,id uuid,version_id uuid,title text,version_label text,content_hash text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT * FROM ai_eligible_sources(NULL) s WHERE s.kind IN('KnowledgeSource','LearnPost')
$$;

-- 3. BIM facts pinned for a ScenarioDraft: the accepted versioned Geometry artifact of the revision (current succeeded attempt,
-- Passed QA, no blocking issue), its floors and semantic anchors, the latest annotation overlay, QA findings and derived
-- quality flags, and the newest active runtime catalog entry. NULL when the revision has no accepted geometry.
CREATE FUNCTION ai_bim_snapshot(p_revision uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE rev revisions;geo revision_artifacts;run uuid;flags jsonb;total integer;cat runtime_compatibility_catalog;
BEGIN
 SELECT * INTO rev FROM revisions WHERE id=p_revision AND status IN('ReadyForScenario','ConfirmedForTraining');
 IF rev.id IS NULL THEN RETURN NULL;END IF;
 SELECT a.* INTO geo FROM revision_artifacts a
  JOIN processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
  JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND pa.processing_job_id=j.id AND j.current_attempt_id=pa.id
  WHERE a.revision_id=rev.id AND a.artifact_type='preview_glb' AND a.metadata->>'schemaVersion'='fet3d.editor/1'
  AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded'
  AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=j.id AND vr.processing_attempt_id=pa.id AND vr.revision_id=a.revision_id
   AND vr.kind='Geometry' AND vr.outcome='Passed' AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')))
  ORDER BY a.created_at DESC,a.id LIMIT 1;
 IF geo.id IS NULL THEN RETURN NULL;END IF;
 SELECT vr.id INTO run FROM validation_runs vr WHERE vr.job_id=geo.job_id AND vr.processing_attempt_id=geo.attempt_id AND vr.kind='Geometry' AND vr.outcome='Passed'
  ORDER BY vr.created_at DESC,vr.id LIMIT 1;
 SELECT COALESCE(jsonb_agg(jsonb_build_object('code',i.code,'severity',i.severity,'message',left(i.message,500),'ifcGlobalId',i.ifc_guid) ORDER BY i.created_at,i.id),'[]')
  INTO flags FROM revision_issues i WHERE i.validation_run_id=run;
 total:=CASE WHEN jsonb_typeof(geo.metadata->'semanticMapping')='array' THEN jsonb_array_length(geo.metadata->'semanticMapping') ELSE 0 END;
 IF total=0 THEN flags:=flags||jsonb_build_array(jsonb_build_object('code','SEMANTIC_MAPPING_EMPTY','severity','Warning'));END IF;
 IF total>0 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(geo.metadata->'semanticMapping') m WHERE m->>'semanticType' IN('IfcDoor','IfcStair','IfcStairFlight','IfcRamp')) THEN
  flags:=flags||jsonb_build_array(jsonb_build_object('code','EXIT_MAPPING_MISSING','severity','Warning'));END IF;
 IF total>0 AND EXISTS(SELECT 1 FROM jsonb_array_elements(geo.metadata->'semanticMapping') m
   WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(geo.metadata->'floors') f WHERE f->>'id'=m->>'floorId')) THEN
  flags:=flags||jsonb_build_array(jsonb_build_object('code','FLOOR_MAPPING_INCOMPLETE','severity','Warning'));END IF;
 IF total>5000 THEN flags:=flags||jsonb_build_array(jsonb_build_object('code','ANCHORS_TRUNCATED','severity','Info'));END IF;
 SELECT * INTO cat FROM runtime_compatibility_catalog WHERE is_active ORDER BY created_at DESC,id LIMIT 1;
 IF cat.id IS NULL THEN flags:=flags||jsonb_build_array(jsonb_build_object('code','RUNTIME_CATALOG_EMPTY','severity','Warning'));END IF;
 RETURN jsonb_build_object('revisionId',rev.id,'buildingId',rev.building_id,'versionLabel',rev.version_label,'revisionStatus',rev.status,
  'geometry',jsonb_build_object('artifactId',geo.id,'sha256Hash',geo.sha256_hash,'schemaVersion',geo.metadata->>'schemaVersion'),
  'units',geo.metadata->'units','upAxis',geo.metadata->'upAxis','handedness',geo.metadata->'handedness','coordinateTransform',geo.metadata->'coordinateTransform',
  'floors',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',f->'id','name',f->'name','elevationMeters',f->'elevationMeters','ifcGlobalId',f->'ifcGlobalId') ORDER BY o)
   FROM jsonb_array_elements(geo.metadata->'floors') WITH ORDINALITY e(f,o)),'[]'),
  'anchors',COALESCE((SELECT jsonb_agg(x.m ORDER BY x.o) FROM (SELECT jsonb_build_object('ifcGlobalId',m->'ifcGlobalId','nodeId',m->'nodeId','floorId',m->'floorId','semanticType',m->'semanticType') m,o
   FROM jsonb_array_elements(CASE WHEN total>0 THEN geo.metadata->'semanticMapping' ELSE '[]' END) WITH ORDINALITY e(m,o) ORDER BY o LIMIT 5000) x),'[]'),
  'annotations',COALESCE((SELECT s.data->'items' FROM annotation_sets s WHERE s.revision_id=rev.id ORDER BY s.version_number DESC LIMIT 1),'[]'),
  'qualityFlags',flags,
  'runtime',CASE WHEN cat.id IS NULL THEN NULL ELSE jsonb_build_object('runtimeVersion',cat.runtime_version,'protocolVersion',cat.protocol_version,
   'capabilities',cat.capabilities,'capabilityContracts',cat.capability_contracts) END);
END $$;

-- 4. Trainee context: a learner-safe Training snapshot (no rubric, scoring or scenario state) only while the Trainee may play
-- it, and the Trainee's own saved session result.
CREATE FUNCTION ai_trainee_training_snapshot(p_actor uuid,p_training uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('trainingId',t.id,'trainingName',t.name,'trainingDescription',t.description,'buildingId',b.id,'buildingName',b.name,
  'scenarioVersionId',sv.id,'scenarioName',sv.name,'learningObjectives',sv.learning_objectives,'learnerInstructions',sv.learner_instructions,
  'allowedModes',to_jsonb(t.allowed_modes),'timeLimitSeconds',sv.time_limit_seconds)
 FROM trainings t JOIN releases rel ON rel.id=t.release_id JOIN buildings b ON b.id=rel.building_id JOIN scenario_versions sv ON sv.id=rel.scenario_version_id
  JOIN release_build_provenance pin ON pin.release_id=rel.id JOIN scenario_content_reviews review ON review.id=pin.content_review_id AND review.status='Approved'
 WHERE t.id=p_training AND t.status='Active' AND rel.status='Published' AND b.is_active AND b.deleted_at IS NULL
  AND (t.start_date IS NULL OR t.start_date<=clock_timestamp()) AND (t.end_date IS NULL OR t.end_date>clock_timestamp())
  AND (b.visibility='Public' OR EXISTS(SELECT 1 FROM building_participation_grants g WHERE g.building_id=b.id AND g.trainee_user_id=p_actor AND g.access_revision=b.access_revision))
 LIMIT 1
$$;

CREATE FUNCTION ai_trainee_result_snapshot(p_actor uuid,p_session uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('sessionId',s.id,'trainingId',s.training_id,'mode',r.mode,'outcome',r.outcome,'reason',r.outcome_reason,'scorePercent',r.score,
  'criteria',r.criterion_results,'metrics',r.metrics,'completedAt',r.created_at)
 FROM sessions s JOIN session_results r ON r.session_id=s.id WHERE s.id=p_session AND s.trainee_user_id=p_actor AND s.contract_version=7
$$;

-- Units a Trainee holds today (Asia/Ho_Chi_Minh): settled usage plus reservations not yet released.
CREATE FUNCTION ai_trainee_units_today(p_actor uuid) RETURNS TABLE(used bigint,reserved bigint,day_start timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH d AS (SELECT (date_trunc('day',clock_timestamp() AT TIME ZONE 'Asia/Ho_Chi_Minh') AT TIME ZONE 'Asia/Ho_Chi_Minh') s)
 SELECT COALESCE(sum(r.consumed_units) FILTER (WHERE r.status='Succeeded'),0),
  COALESCE(sum(r.reserved_units) FILTER (WHERE r.status IN('Accepted','Processing','NeedsReconcile')),0),d.s
 FROM d LEFT JOIN ai_requests r ON r.audience='trainee' AND r.actor_user_id=p_actor AND r.created_at>=d.s GROUP BY d.s
$$;

-- 5. Representation. Viewer organization|trainee|admin. Citations are resolved against the pinned sources, so title, version
-- and link come from the pin. A Trainee no longer allowed to see the context (Training access lost, Learn post removed) sees
-- the request without its result.
CREATE FUNCTION ai_request_view(p_id uuid,p_viewer text) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
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
   'revisionId',r.bim->'revisionId','requestedSources',COALESCE(r.input->'sources','[]'),'context',r.context-'snapshot'),
  'sources',COALESCE((SELECT jsonb_agg(s-'content' ORDER BY o) FROM jsonb_array_elements(r.sources) WITH ORDINALITY e(s,o)),'[]'),
  'result',CASE WHEN withheld OR r.result IS NULL THEN NULL ELSE r.result-'citations'-'ruleResults' END,
  'citations',CASE WHEN withheld THEN '[]'::jsonb ELSE COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',p->'kind','id',p->'id','versionId',p->'versionId',
    'title',p->'title','versionLabel',p->'versionLabel','chunkId',c->'chunkId','locator',c->'locator','relevance',c->'relevance','link',p->'link') ORDER BY o)
   FROM jsonb_array_elements(COALESCE(r.result->'citations','[]')) WITH ORDINALITY x(c,o)
   CROSS JOIN LATERAL (SELECT p FROM jsonb_array_elements(r.sources) p WHERE p->>'id'=c->>'documentId' AND p->>'versionId'=c->>'documentVersion' LIMIT 1) pp),'[]') END,
  'failureCode',r.failure_code,'resultWithheld',CASE WHEN p_viewer='trainee' THEN withheld END,
  'createdAt',r.created_at,'completedAt',r.completed_at,'revision',r.revision);
 IF p_viewer<>'admin' THEN RETURN base;END IF;
 RETURN base||jsonb_build_object('organizationId',r.organization_id,'actorUserId',r.actor_user_id,'policyId',r.policy_id,'attempts',r.attempts,
  'modelVersion',r.model_version,'technicalUsage',r.technical_usage,'reconcileRequestedAt',r.reconcile_requested_at,'providerResult',r.provider_result,
  'updatedAt',r.updated_at,'bim',r.bim,
  'allocations',(SELECT jsonb_agg(jsonb_build_object('grantId',a.grant_id,'reservedUnits',a.reserved_units,'consumedUnits',a.consumed_units,'status',a.status) ORDER BY a.created_at,a.grant_id)
   FROM billing_ai_quota_allocations a WHERE a.request_id=r.id));
END $$;

CREATE OR REPLACE FUNCTION ai_request_representation(p_id uuid,p_admin boolean) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT ai_request_view(p_id,CASE WHEN p_admin THEN 'admin' ELSE 'organization' END)
$$;

-- Free units of an organization in one quota unit now.
CREATE FUNCTION ai_org_available_units(p_org uuid,p_unit text,p_stamp timestamptz) RETURNS bigint LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT COALESCE(sum(greatest(gr.quota_units-u.used,0)),0)::bigint FROM billing_ai_quota_grants gr
  CROSS JOIN LATERAL (SELECT COALESCE(sum(CASE a.status WHEN 'Reserved' THEN a.reserved_units WHEN 'Settled' THEN a.consumed_units ELSE 0 END),0) used
   FROM billing_ai_quota_allocations a WHERE a.grant_id=gr.id) u
  WHERE gr.organization_id=p_org AND gr.quota_unit=p_unit AND gr.starts_at<=p_stamp AND gr.ends_at>p_stamp
$$;

-- 6. Organization entry points: Sources, Limits, Submit, Get.
CREATE OR REPLACE FUNCTION ai_org_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;pol ai_operation_policies;r ai_requests;receipt ai_command_receipts;g record;rv record;
 h text;pinned jsonb;requested jsonb;pinned_bim jsonb;building uuid;remaining integer;take integer;available bigint;stamp timestamptz:=clock_timestamp();
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
  -- Pin one revision with accepted geometry: the requested one, or the newest revision of the Building that has it.
  IF NULLIF(p_input->'request'->>'revisionId','') IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM revisions v WHERE v.id::text=p_input->'request'->>'revisionId' AND v.building_id=building) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   pinned_bim:=ai_bim_snapshot((p_input->'request'->>'revisionId')::uuid);
  ELSE
   FOR rv IN SELECT v.id FROM revisions v WHERE v.building_id=building AND v.status IN('ReadyForScenario','ConfirmedForTraining') ORDER BY v.created_at DESC,v.id LIMIT 20 LOOP
    pinned_bim:=ai_bim_snapshot(rv.id);EXIT WHEN pinned_bim IS NOT NULL;
   END LOOP;
  END IF;
  IF pinned_bim IS NULL THEN RETURN jsonb_build_object('code','AI_REVISION_NOT_READY','status',409);END IF;
 END IF;
 -- Pin sources: the requested set must be eligible for this tenant now; an empty request pins every eligible source up to the policy limit.
 requested:=COALESCE(p_input->'request'->'sources','[]');
 IF jsonb_array_length(requested)>pol.max_sources THEN RETURN jsonb_build_object('code','AI_TOO_MANY_SOURCES','status',422);END IF;
 IF jsonb_array_length(requested)>0 AND EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE NOT EXISTS(SELECT 1 FROM ai_eligible_sources(org.id) s WHERE s.kind=e->>'kind' AND s.id::text=e->>'id')) THEN
  RETURN jsonb_build_object('code','AI_SOURCE_NOT_ALLOWED','status',422);END IF;
 SELECT COALESCE(jsonb_agg(x.item ORDER BY x.kind,x.title,x.id),'[]') INTO pinned FROM (
  SELECT s.kind,s.title,s.id,ai_source_pin(s.kind,s.id,s.version_id,s.title,s.version_label,s.content_hash) item FROM ai_eligible_sources(org.id) s
  WHERE jsonb_array_length(requested)=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE s.kind=e->>'kind' AND s.id::text=e->>'id')
  ORDER BY s.kind,s.title,s.id LIMIT pol.max_sources) x;
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
 VALUES(gen_random_uuid(),p_actor,org.id,'User','Create','AiRequest',r.id,gen_random_uuid(),jsonb_build_object('operation',r.operation,'policyId',pol.id,'reservedUnits',r.reserved_units,'quotaUnit',r.quota_unit,'revisionId',pinned_bim->'revisionId'),now());
 RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'organization'));
END $$;

-- 7. Trainee entry points: Quota, Submit, Get, List.
CREATE FUNCTION ai_trainee_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;pol ai_operation_policies;r ai_requests;receipt ai_command_receipts;u record;h text;pinned jsonb:='[]';ctx jsonb;snap jsonb;
 requested jsonb;lp record;page integer;size integer;total integer;result jsonb;assessment boolean;stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Quota','Submit','Get','List') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO pol FROM ai_operation_policies WHERE operation='TraineeAnswer' AND effective_from<=stamp AND (effective_until IS NULL OR effective_until>stamp);
 -- Assessment attempts must be the Trainee's own work: no AI while one is open.
 assessment:=EXISTS(SELECT 1 FROM sessions s WHERE s.trainee_user_id=p_actor AND s.contract_version=7 AND s.mode='Assessment'
  AND s.status IN('Created','Launching','Running'));

 IF p_action='Quota' THEN
  SELECT * INTO u FROM ai_trainee_units_today(p_actor);
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('policyActive',pol.id IS NOT NULL,'quotaUnit',pol.quota_unit,'dailyUnits',pol.daily_units,
   'usedUnits',u.used,'reservedUnits',u.reserved,'remainingUnits',CASE WHEN pol.id IS NULL THEN 0 ELSE greatest(pol.daily_units-u.used-u.reserved,0) END,
   'estimatedReserve',pol.reserve_units,'maxInputChars',pol.max_input_chars,'resetsAt',u.day_start+interval '1 day','blockedByAssessment',assessment,
   'canSubmit',pol.id IS NOT NULL AND NOT assessment AND pol.daily_units-u.used-u.reserved>=pol.reserve_units));
 ELSIF p_action='Get' THEN
  IF NOT EXISTS(SELECT 1 FROM ai_requests WHERE id=p_resource AND audience='trainee' AND actor_user_id=p_actor) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_view(p_resource,'trainee'));
 ELSIF p_action='List' THEN
  page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);
  IF page<1 OR size NOT BETWEEN 1 AND 100 OR (p_input->>'status' IS NOT NULL AND p_input->>'status' NOT IN('Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected')) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  WITH f AS MATERIALIZED(SELECT id,created_at FROM ai_requests WHERE audience='trainee' AND actor_user_id=p_actor AND (p_input->>'status' IS NULL OR status=p_input->>'status'))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(ai_request_view(x.id,'trainee') ORDER BY x.created_at DESC,x.id)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 END IF;

 -- Submit
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 h:=fet3d_jsonb_payload_hash(p_input);
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-receipt:'||p_actor||':'||p_key,0));
 SELECT * INTO receipt FROM ai_command_receipts WHERE actor_user_id=p_actor AND operation='TraineeAiSubmit' AND idempotency_key=p_key;
 IF receipt.actor_user_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_view((receipt.result->>'requestId')::uuid,'trainee'),'replayed',true);
 END IF;
 IF assessment THEN RETURN jsonb_build_object('code','AI_BLOCKED_DURING_ASSESSMENT','status',409);END IF;
 IF pol.id IS NULL THEN RETURN jsonb_build_object('code','AI_POLICY_UNAVAILABLE','status',503);END IF;
 IF length(COALESCE(p_input->>'question',''))>pol.max_input_chars THEN RETURN jsonb_build_object('code','AI_INPUT_TOO_LARGE','status',422);END IF;
 -- Context: a Published Learn post version, a Training the Trainee may play, or the Trainee's own saved result.
 CASE COALESCE(p_input->'context'->>'kind','None')
  WHEN 'None' THEN ctx:=NULL;
  WHEN 'Learn' THEN
   SELECT p.id,p.slug,v.id version_id,v.title,v.version_number,v.content_hash INTO lp FROM learn_posts p JOIN learn_post_versions v ON v.id=p.published_version_id
    WHERE p.id::text=p_input->'context'->>'postId' AND v.id::text=p_input->'context'->>'versionId' AND p.publication_status='Published' AND learn_rag_eligible(p.id,v.id);
   IF NOT FOUND THEN RETURN jsonb_build_object('code','AI_CONTEXT_NOT_FOUND','status',404);END IF;
   ctx:=jsonb_build_object('kind','Learn','postId',lp.id,'versionId',lp.version_id,'slug',lp.slug);
   pinned:=jsonb_build_array(ai_source_pin('LearnPost',lp.id,lp.version_id,lp.title,'v'||lp.version_number,lp.content_hash));
  WHEN 'Training' THEN
   snap:=ai_trainee_training_snapshot(p_actor,NULLIF(p_input->'context'->>'trainingId','')::uuid);
   IF snap IS NULL THEN RETURN jsonb_build_object('code','AI_CONTEXT_NOT_FOUND','status',404);END IF;
   ctx:=jsonb_build_object('kind','Training','trainingId',snap->'trainingId','buildingId',snap->'buildingId','scenarioVersionId',snap->'scenarioVersionId');
   pinned:=jsonb_build_array(ai_source_pin('TrainingSnapshot',(snap->>'trainingId')::uuid,(snap->>'scenarioVersionId')::uuid,snap->>'trainingName','snapshot',
    encode(sha256(convert_to(snap::text,'UTF8')),'hex'))||jsonb_build_object('content',snap));
  WHEN 'Result' THEN
   snap:=ai_trainee_result_snapshot(p_actor,NULLIF(p_input->'context'->>'sessionId','')::uuid);
   IF snap IS NULL THEN RETURN jsonb_build_object('code','AI_CONTEXT_NOT_FOUND','status',404);END IF;
   ctx:=jsonb_build_object('kind','Result','sessionId',snap->'sessionId','trainingId',snap->'trainingId');
   pinned:=jsonb_build_array(ai_source_pin('SessionResult',(snap->>'sessionId')::uuid,(snap->>'sessionId')::uuid,'Session result',COALESCE(snap->>'mode','result'),
    encode(sha256(convert_to(snap::text,'UTF8')),'hex'))||jsonb_build_object('content',snap));
  ELSE RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);
 END CASE;
 requested:=COALESCE(p_input->'sources','[]');
 IF jsonb_array_length(requested)>pol.max_sources THEN RETURN jsonb_build_object('code','AI_TOO_MANY_SOURCES','status',422);END IF;
 IF jsonb_array_length(requested)>0 AND EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE NOT EXISTS(SELECT 1 FROM ai_trainee_eligible_sources() s WHERE s.kind=e->>'kind' AND s.id::text=e->>'id')) THEN
  RETURN jsonb_build_object('code','AI_SOURCE_NOT_ALLOWED','status',422);END IF;
 pinned:=pinned||COALESCE((SELECT jsonb_agg(x.item ORDER BY x.kind,x.title,x.id) FROM (
  SELECT s.kind,s.title,s.id,ai_source_pin(s.kind,s.id,s.version_id,s.title,s.version_label,s.content_hash) item FROM ai_trainee_eligible_sources() s
  WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(pinned) q WHERE q->>'kind'=s.kind AND q->>'id'=s.id::text)
   AND (jsonb_array_length(requested)=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(requested) e WHERE s.kind=e->>'kind' AND s.id::text=e->>'id'))
  ORDER BY s.kind,s.title,s.id LIMIT pol.max_sources) x),'[]');
 IF jsonb_array_length(pinned)=0 THEN RETURN jsonb_build_object('code','AI_NO_SOURCES','status',422);END IF;
 -- Daily allowance per Trainee; the lock makes concurrent submits of one Trainee see each other's reservations.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-trainee-quota:'||p_actor,0));
 SELECT * INTO u FROM ai_trainee_units_today(p_actor);
 IF u.used+u.reserved+pol.reserve_units>pol.daily_units THEN RETURN jsonb_build_object('code','AI_DAILY_QUOTA_EXHAUSTED','status',409);END IF;
 INSERT INTO ai_requests(id,organization_id,actor_user_id,audience,operation,policy_id,quota_unit,reserved_units,status,input,input_hash,sources,context,next_attempt_at,created_at,updated_at)
 VALUES(gen_random_uuid(),NULL,p_actor,'trainee','TraineeAnswer',pol.id,pol.quota_unit,pol.reserve_units,'Accepted',
  jsonb_build_object('question',p_input->>'question','sources',requested,'context',p_input->'context'),h,pinned,ctx,stamp,stamp,stamp) RETURNING * INTO r;
 INSERT INTO ai_command_receipts VALUES(p_actor,'TraineeAiSubmit',p_key,h,jsonb_build_object('requestId',r.id),stamp);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User','Create','AiRequest',r.id,gen_random_uuid(),jsonb_build_object('operation',r.operation,'policyId',pol.id,'reservedUnits',r.reserved_units,'context',ctx->>'kind'),now());
 RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'trainee'));
END $$;

-- 8. Worker: Claim returns the v2 dispatch payload; Complete settles a normalized v2 result.
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
  -- The provider may name why it refused the pinned input; anything else stays the generic code.
  UPDATE ai_requests SET status='Failed',failure_code=CASE WHEN p_input->>'code' IN('AI_SOURCE_HASH_MISMATCH','AI_SOURCE_UNAVAILABLE','AI_REVISION_NOT_PINNED')
   THEN p_input->>'code' ELSE 'AI_PROVIDER_REJECTED' END,lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSE -- NotFound: the provider has no record of the stable request id, so nothing was consumed.
  IF phase<>'Lookup' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  UPDATE ai_requests SET status='Failed',failure_code='AI_REQUEST_NOT_RECEIVED',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 END IF;
 SELECT * INTO r FROM ai_requests WHERE id=r.id;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status',r.status,'failureCode',r.failure_code));
END $$;

-- 9. Organization request list with the v7 vocabulary and resultType.
CREATE OR REPLACE FUNCTION ai_org_request_list_gate(p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;page integer;size integer;total integer;result jsonb;mine boolean;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);mine:=COALESCE((p_input->>'mine')::boolean,false);
 IF page<1 OR size NOT BETWEEN 1 AND 100
  OR (p_input->>'status' IS NOT NULL AND p_input->>'status' NOT IN('Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected'))
  OR (p_input->>'resultType' IS NOT NULL AND p_input->>'resultType' NOT IN('KnowledgeAnswer','ScenarioDraft','InsufficientEvidence','RejectedBySafetyGate'))
  OR (p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer'))
  THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 WITH f AS MATERIALIZED(SELECT id,created_at FROM ai_requests WHERE organization_id=actor.organization_id
   AND (p_input->>'status' IS NULL OR status=p_input->>'status') AND (p_input->>'resultType' IS NULL OR result_type=p_input->>'resultType')
   AND (p_input->>'operation' IS NULL OR operation=p_input->>'operation') AND (NOT mine OR actor_user_id=p_actor))
 SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(ai_request_view(x.id,'organization') ORDER BY x.created_at DESC,x.id)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
END $$;

-- 10. Admin: request filters by operation and audience, paginated policies, TraineeAnswer policies with dailyUnits.
CREATE OR REPLACE FUNCTION ai_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
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
  IF (p_input->>'status' IS NOT NULL AND p_input->>'status' NOT IN('Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected'))
   OR (p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer','TraineeAnswer'))
   OR (p_input->>'audience' IS NOT NULL AND p_input->>'audience' NOT IN('organization','trainee')) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  WITH f AS MATERIALIZED(SELECT id,created_at FROM ai_requests WHERE (p_input->>'status' IS NULL OR status=p_input->>'status')
    AND (p_input->>'operation' IS NULL OR operation=p_input->>'operation') AND (p_input->>'audience' IS NULL OR audience=p_input->>'audience')
    AND (p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(ai_request_view(x.id,'admin') ORDER BY x.created_at DESC,x.id)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='GetRequest' THEN
  IF NOT EXISTS(SELECT 1 FROM ai_requests WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_view(p_resource,'admin'));
 ELSIF p_action='ListPolicies' THEN
  IF p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer','TraineeAnswer') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  SELECT count(*) INTO total FROM ai_operation_policies WHERE p_input->>'operation' IS NULL OR operation=p_input->>'operation';
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.operation,x.effective_from DESC,x.id),'[]') INTO result FROM (SELECT id,operation,effective_from,jsonb_build_object('id',id,'operation',operation,
   'quotaUnit',quota_unit,'reserveUnits',reserve_units,'maxInputChars',max_input_chars,'maxSources',max_sources,'dailyUnits',daily_units,'effectiveFrom',effective_from,
   'effectiveUntil',effective_until,'createdAt',created_at) item FROM ai_operation_policies WHERE p_input->>'operation' IS NULL OR operation=p_input->>'operation'
   ORDER BY operation,effective_from DESC,id OFFSET (page-1)::bigint*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='ListSources' THEN
  SELECT count(*) INTO total FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status';
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.created_at DESC,x.id),'[]') INTO result FROM (SELECT id,created_at,jsonb_build_object('id',id,'visibility',visibility,'organizationId',organization_id,'title',title,
   'sourceUri',source_uri,'versionLabel',version_label,'sourceHash',source_hash,'jurisdiction',jurisdiction,'effectiveFrom',effective_from,'effectiveUntil',effective_until,'approvalStatus',approval_status,
   'approvedAt',approved_at,'createdAt',created_at,'revision',revision) item FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status'
   ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x;
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
  result:=ai_request_view(r.id,'admin');
 ELSIF p_action='CreatePolicy' THEN
  BEGIN
   INSERT INTO ai_operation_policies(id,operation,quota_unit,reserve_units,max_input_chars,max_sources,daily_units,effective_from,effective_until,created_by,created_at)
   VALUES(gen_random_uuid(),p_input->>'operation',p_input->>'quotaUnit',(p_input->>'reserveUnits')::int,(p_input->>'maxInputChars')::int,(p_input->>'maxSources')::int,
    (p_input->>'dailyUnits')::int,COALESCE((p_input->>'effectiveFrom')::timestamptz,stamp),(p_input->>'effectiveUntil')::timestamptz,p_actor,stamp) RETURNING * INTO pol;
  EXCEPTION WHEN exclusion_violation THEN RETURN jsonb_build_object('code','AI_POLICY_OVERLAP','status',409);
   WHEN check_violation OR not_null_violation OR invalid_text_representation OR datetime_field_overflow OR numeric_value_out_of_range THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
  END;
  result:=jsonb_build_object('id',pol.id,'operation',pol.operation,'quotaUnit',pol.quota_unit,'reserveUnits',pol.reserve_units,'maxInputChars',pol.max_input_chars,'maxSources',pol.max_sources,
   'dailyUnits',pol.daily_units,'effectiveFrom',pol.effective_from,'effectiveUntil',pol.effective_until,'createdAt',pol.created_at);
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

-- 10b. ai_source_states (#91): whether each source version may still be used. Learn and Library content is sent inline with each request, so the indexer only
-- needs KnowledgeSource rows to keep or drop chunks; Learn and Library rows are listed for caches the service may keep.
CREATE OR REPLACE VIEW ai_source_states WITH (security_barrier) AS
 SELECT 'KnowledgeSource'::text kind,k.id,k.id version_id,k.organization_id,k.visibility::text visibility,k.source_hash::text content_hash,k.source_uri,
  (k.approval_status='Approved' AND (k.effective_from IS NULL OR k.effective_from<=now()) AND (k.effective_until IS NULL OR k.effective_until>now())) eligible,
  k.approval_status::text status
 FROM knowledge_sources k
 UNION ALL
 SELECT 'LearnPost',p.id,v.id,NULL::uuid,'Common',v.content_hash::text,NULL::text,learn_rag_eligible(p.id,v.id),p.publication_status::text
 FROM learn_posts p JOIN learn_post_versions v ON v.post_id=p.id WHERE v.status='Published'
 UNION ALL
 SELECT 'LibraryVersion',v.id,v.id,NULL::uuid,'Common',v.payload_hash::text,NULL::text,(v.status='Published' AND i.is_active),v.status::text
 FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id;
-- Owned by the gate owner so it can call learn_rag_eligible; the indexer reads it and nothing underneath.
DO $$ DECLARE r text;BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_ai_indexer') THEN CREATE ROLE fet3d_ai_indexer NOLOGIN;END IF;
 ALTER VIEW ai_source_states OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON ai_source_states FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON ai_source_states FROM %I',r);END IF;
 END LOOP;
 GRANT SELECT ON ai_source_states TO fet3d_ai_indexer;
END $$;

-- 11. Privileges: helpers and gates run as the gate owner; only the gates are callable by the API logins.
DO $grants$ DECLARE t text;r text;sig text;s record;BEGIN
 -- Reads the new helpers need. Tables under RLS without a policy for the owner get a read-only one.
 FOREACH t IN ARRAY ARRAY['revisions','revision_artifacts','processing_jobs','processing_job_attempts','validation_runs','revision_issues','annotation_sets',
   'runtime_compatibility_catalog','learn_posts','learn_post_versions','organization_library_items','organization_library_versions','knowledge_sources',
   'sessions','session_results','trainings','releases','scenario_versions','buildings','building_participation_grants','release_build_provenance','scenario_content_reviews'] LOOP
  EXECUTE format('GRANT SELECT ON %I TO fet3d_ifc_upload_owner',t);
  IF (SELECT relrowsecurity FROM pg_class WHERE oid=format('public.%I',t)::regclass) AND NOT EXISTS(SELECT 1 FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=t
     AND ('fet3d_ifc_upload_owner'=ANY(p.roles) OR 'public'=ANY(p.roles)) AND p.cmd IN('ALL','SELECT')) THEN
   EXECUTE format('CREATE POLICY ai_gate_owner_read ON %I FOR SELECT TO fet3d_ifc_upload_owner USING(true)',t);
  END IF;
 END LOOP;
 FOREACH sig IN ARRAY ARRAY['ai_source_link(text,uuid)','ai_source_pin(text,uuid,uuid,text,text,text)','ai_source_content(text,uuid)','ai_trainee_eligible_sources()',
   'ai_bim_snapshot(uuid)','ai_trainee_training_snapshot(uuid,uuid)','ai_trainee_result_snapshot(uuid,uuid)','ai_trainee_units_today(uuid)','ai_request_view(uuid,text)',
   'ai_request_representation(uuid,boolean)','ai_org_available_units(uuid,text,timestamp with time zone)','ai_org_gate(text,uuid,uuid,uuid,jsonb,text)',
   'ai_trainee_gate(text,uuid,uuid,uuid,jsonb,text)','ai_worker_gate(text,text,uuid,uuid,jsonb)','ai_org_request_list_gate(uuid,uuid,jsonb)',
   'ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION ai_org_gate(text,uuid,uuid,uuid,jsonb,text),ai_trainee_gate(text,uuid,uuid,uuid,jsonb,text),ai_worker_gate(text,text,uuid,uuid,jsonb),'
   'ai_org_request_list_gate(uuid,uuid,jsonb),ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM ai_contract_v2_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
