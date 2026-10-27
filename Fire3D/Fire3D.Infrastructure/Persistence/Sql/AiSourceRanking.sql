-- #178: pick the sources an AI request pins by relevance instead of 'the first N by kind and title'. When the caller names no
-- sources, the gates used to pin ai_eligible_sources ORDER BY kind,title LIMIT max_sources, so a question about e-bike charging
-- got 'American Red Cross…', 'GOV.UK…' and never the regulation the lesson cites, and Library versions (sorted after the other
-- kinds) were almost never pinned. Now, within the eligible sources:
-- 1. TraineeAnswer in a Learn post: the post itself (as before), then the knowledge sources its published version cites.
-- 2. ScenarioDraft: Library versions (scenario templates, rubric samples, equipment) before documents; the BIM facts are pinned
--    separately as before.
-- 3. Then by a lexical relevance score of the question (and the Learn post title) against each source's title and text:
--    diacritics and case folded as in Learn search, Vietnamese/English function words dropped, rarer words weigh more
--    (ln(1+N/df)), a title match counts double. Ties keep the old kind/title/id order, so with no matching word nothing changes.
-- The AI service still retrieves only within the pinned set; vector retrieval over the whole index needs a contract change
-- with the AI repo (AI#24) and is not part of this migration.
-- The gates keep their signatures, so owner and EXECUTE grants are unchanged by OR REPLACE. Only the pinning block differs from
-- AiContractV2.sql; the new helpers are owned by fet3d_ifc_upload_owner and not executable by any API role.
CREATE TEMP TABLE ai_source_ranking_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_source_ranking_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Distinct folded words of 2+ letters or digits ('Sạc xe máy điện' -> {sac,xe,may,dien}).
CREATE OR REPLACE FUNCTION ai_source_words(p_text text) RETURNS text[] LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog,public AS $$
 SELECT COALESCE(array_agg(DISTINCT w),'{}') FROM regexp_split_to_table(learn_fold_text(COALESCE(p_text,'')),'[^a-z0-9]+') w WHERE length(w)>=2
$$;

-- Words of a question that say nothing about its subject (folded Vietnamese and English function words).
CREATE OR REPLACE FUNCTION ai_question_terms(p_text text) RETURNS text[] LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog,public AS $$
 SELECT ARRAY(SELECT w FROM unnest(ai_source_words(p_text)) w WHERE w<>ALL(ARRAY[
  'va','la','cua','co','khong','cho','nao','gi','khi','thi','duoc','mot','nhung','cac','trong','voi','de','toi','ban','minh','nay','do','neu','hay',
  'phai','nhu','tai','ve','tu','den','bi','se','da','dang','can','lam','sao','vi','ra','vao','len','con','chua','nen','hoac','theo','bang','tren',
  'duoi','sau','truoc','ai','day','kia','ay','roi','rat','lai','cung','moi','nhieu','it','hon','nhat','ma','em','anh','chi','oi',
  'the','and','or','of','to','in','on','for','is','are','was','be','what','how','why','when','where','which','who','do','does','can','should',
  'my','your','it','this','that','with','at','by','from','an','as','if','not','no','me','we','you','they','about','into','there']))
$$;

-- Text a source is matched on besides its title: a document's jurisdiction and label plus the titles and summaries of the
-- published Learn posts that cite it (so a Vietnamese question finds an English guide a lesson relies on), a Learn post's
-- summary, a Library version's kind and payload.
CREATE OR REPLACE FUNCTION ai_source_text(p_kind text,p_id uuid,p_version uuid) RETURNS text LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT CASE p_kind
  WHEN 'KnowledgeSource' THEN (SELECT concat_ws(' ',k.jurisdiction,k.version_label,(SELECT string_agg(v.title||' '||v.summary,' ')
    FROM learn_post_version_sources l JOIN learn_posts p ON p.published_version_id=l.post_version_id JOIN learn_post_versions v ON v.id=l.post_version_id
    WHERE l.source_id=k.id)) FROM knowledge_sources k WHERE k.id=p_id)
  WHEN 'LearnPost' THEN (SELECT v.summary FROM learn_post_versions v WHERE v.id=p_version)
  WHEN 'LibraryVersion' THEN (SELECT concat_ws(' ',i.kind,left(v.payload::text,8000)) FROM organization_library_versions v
    JOIN organization_library_items i ON i.id=v.item_id WHERE v.id=p_version)
 END
$$;

-- Pins for one request, most useful first, at most p_limit. Candidates are the eligible sources of the audience (a Trainee:
-- Common documents and Learn posts; an organization: its own and Common documents, Library, Learn), minus those already in
-- p_pinned, restricted to p_requested when that is not empty. Order: the sources p_cited_version cites (Learn context), then
-- for ScenarioDraft the Library versions, then relevance, then the previous kind/title/id order.
CREATE OR REPLACE FUNCTION ai_pick_sources(p_org uuid,p_trainee boolean,p_operation text,p_text text,p_requested jsonb,p_pinned jsonb,p_cited_version uuid,p_limit integer)
RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH q AS (SELECT ai_question_terms(p_text) terms),
 s AS (
  SELECT e.* FROM ai_trainee_eligible_sources() e WHERE p_trainee
  UNION ALL
  SELECT e.* FROM ai_eligible_sources(p_org) e WHERE NOT p_trainee),
 c AS MATERIALIZED (
  SELECT s.*,ai_source_words(s.title) title_words,ai_source_words(ai_source_text(s.kind,s.id,s.version_id)) text_words,
   CASE WHEN s.kind='KnowledgeSource' AND EXISTS(SELECT 1 FROM learn_post_version_sources l WHERE l.post_version_id=p_cited_version AND l.source_id=s.id) THEN 0
    WHEN p_operation='ScenarioDraft' AND s.kind='LibraryVersion' THEN 1 ELSE 2 END tier
  FROM s
  WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(p_pinned,'[]')) x WHERE x->>'kind'=s.kind AND x->>'id'=s.id::text)
   AND (jsonb_array_length(COALESCE(p_requested,'[]'))=0
    OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_requested) e WHERE e->>'kind'=s.kind AND e->>'id'=s.id::text))),
 df AS (SELECT t,count(*) n FROM c CROSS JOIN q CROSS JOIN LATERAL unnest(q.terms) t WHERE t=ANY(c.title_words) OR t=ANY(c.text_words) GROUP BY t),
 r AS (SELECT c.*,COALESCE((SELECT sum(CASE WHEN df.t=ANY(c.title_words) THEN 2 ELSE 1 END*ln(1+(SELECT count(*) FROM c)::numeric/df.n))
   FROM df WHERE df.t=ANY(c.title_words) OR df.t=ANY(c.text_words)),0) score FROM c)
 SELECT COALESCE(jsonb_agg(ai_source_pin(x.kind,x.id,x.version_id,x.title,x.version_label,x.content_hash) ORDER BY x.tier,x.score DESC,x.kind,x.title,x.id),'[]')
 FROM (SELECT * FROM r ORDER BY tier,score DESC,kind,title,id LIMIT p_limit) x
$$;

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
 VALUES(gen_random_uuid(),p_actor,org.id,'User','Create','AiRequest',r.id,gen_random_uuid(),jsonb_build_object('operation',r.operation,'policyId',pol.id,'reservedUnits',r.reserved_units,'quotaUnit',r.quota_unit,'revisionId',pinned_bim->'revisionId'),now());
 RETURN jsonb_build_object('code','OK','result',ai_request_view(r.id,'organization'));
END $$;

CREATE OR REPLACE FUNCTION ai_trainee_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;pol ai_operation_policies;r ai_requests;receipt ai_command_receipts;u record;h text;pinned jsonb:='[]';ctx jsonb;snap jsonb;
 requested jsonb;lp record;learn_title text;learn_version uuid;page integer;size integer;total integer;result jsonb;assessment boolean;stamp timestamptz:=clock_timestamp();
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
   learn_title:=lp.title;learn_version:=lp.version_id;
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
 -- #178: in a Learn post, the sources that post cites come first; then the most relevant to the question and the post title.
 pinned:=pinned||ai_pick_sources(NULL,true,pol.operation,concat_ws(' ',p_input->>'question',learn_title),requested,pinned,learn_version,pol.max_sources);
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

DO $grants$ DECLARE s record;sig text;r text;BEGIN
 FOREACH sig IN ARRAY ARRAY['ai_source_words(text)','ai_question_terms(text)','ai_source_text(text,uuid,uuid)',
   'ai_pick_sources(uuid,boolean,text,text,jsonb,jsonb,uuid,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 SELECT * INTO s FROM ai_source_ranking_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
