-- BE#207/#208: sessions left Launching or silently Running never ended, so training history waited forever, reports counted them
-- as active, and ai_trainee_gate kept the Trainee's AI locked (AI_BLOCKED_DURING_ASSESSMENT) for good because one stale
-- Assessment row still matched status IN('Created','Launching','Running').
-- 1. learner_assessment_live: the AI block counts only an Assessment that is really in progress: a Launching (or Created) row whose
--    launch grant has not expired, or a Running row with no completion request and activity (heartbeat, launch or a received event)
--    within the last 5 minutes. Mobile heartbeats every 60 s while a play is open. A play whose completion was requested is over
--    for the Trainee even while it waits for missing events.
-- 2. learner_session_close_stale: closes a v7 Launching/Running session with no completion request once it has been silent for
--    30 minutes and either its continuation has expired or it has been silent for 24 hours (a phone may still sync an offline play
--    while its continuation lives). It completes at the acknowledged sequence with endReason Abandoned through
--    learner_session_finalize, so the session gets a normal immutable result (Assessment: Incomplete, ENDED_BEFORE_COMPLETION).
--    The API runs it every few minutes (StaleLearnerSessionWorker); the first run after deploy closes the rows already stuck.
-- ai_trainee_gate keeps the body of AiSourceRanking.sql except the assessment check. Its signature is unchanged, so owner and
-- EXECUTE grants stay; the new functions are owned by fet3d_ifc_upload_owner and only the sweep is executable by the API roles.
CREATE TEMP TABLE close_stale_sessions_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO close_stale_sessions_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Latest sign of life of one session: start, launch grant, launch, heartbeat or the last received event.
CREATE FUNCTION learner_session_last_activity(p_id uuid) RETURNS timestamptz LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT greatest(s.started_at,s.grant_issued_at,s.launched_at,s.last_heartbeat_at,(SELECT max(e.received_at) FROM session_events e WHERE e.session_id=s.id))
 FROM sessions s WHERE s.id=p_id
$$;

CREATE FUNCTION learner_assessment_live(p_trainee uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT EXISTS(SELECT 1 FROM sessions s WHERE s.trainee_user_id=p_trainee AND s.contract_version=7 AND s.mode='Assessment' AND (
  (s.status IN('Created','Launching') AND s.grant_expires_at>clock_timestamp())
  OR (s.status='Running' AND s.completion_requested_at IS NULL AND learner_session_last_activity(s.id)>clock_timestamp()-interval '5 minutes')))
$$;

CREATE FUNCTION learner_session_close_stale(p_batch integer DEFAULT 100) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE c record;s sessions;closed integer:=0;stamp timestamptz;last_seen timestamptz;seq bigint;
BEGIN
 IF p_batch IS NULL OR p_batch NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'invalid stale session batch';END IF;
 FOR c IN SELECT x.id FROM sessions x WHERE x.contract_version=7 AND x.status IN('Launching','Running') AND x.completion_requested_at IS NULL
   AND learner_session_last_activity(x.id)<clock_timestamp()-interval '30 minutes'
   AND (x.continuation_expires_at<=clock_timestamp() OR learner_session_last_activity(x.id)<clock_timestamp()-interval '24 hours')
   ORDER BY x.started_at,x.id LIMIT p_batch LOOP
  -- A session a gate is writing right now is skipped until the next run; everything is rechecked under the row lock.
  SELECT * INTO s FROM sessions WHERE id=c.id FOR UPDATE SKIP LOCKED;
  IF s.id IS NULL THEN CONTINUE;END IF;
  stamp:=clock_timestamp();last_seen:=learner_session_last_activity(s.id);
  IF s.status NOT IN('Launching','Running') OR s.completion_requested_at IS NOT NULL OR last_seen>=stamp-interval '30 minutes'
   OR NOT (s.continuation_expires_at<=stamp OR last_seen<stamp-interval '24 hours') THEN CONTINUE;END IF;
  seq:=(learner_session_ack(s.id)->>'acknowledgedSequence')::bigint;
  UPDATE sessions SET completion_key='system:stale-session',completion_last_sequence=seq,completion_end_reason='Abandoned',completion_requested_at=stamp WHERE id=s.id;
  PERFORM learner_session_finalize(s.id);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),s.trainee_user_id,s.organization_id,'System','Update','Session',s.id,gen_random_uuid(),
   jsonb_build_object('operation','CloseStale','previousStatus',s.status::text,'lastEventSequence',seq,'lastActivityAt',last_seen),now());
  closed:=closed+1;
 END LOOP;
 RETURN closed;
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
 -- Assessment attempts must be the Trainee's own work: no AI while one is in progress; a stale row no longer counts (#208).
 assessment:=learner_assessment_live(p_actor);

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
 FOREACH sig IN ARRAY ARRAY['learner_session_last_activity(uuid)','learner_assessment_live(uuid)','learner_session_close_stale(integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION learner_session_close_stale(integer) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM close_stale_sessions_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
