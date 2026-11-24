-- BE#216 (Flow 1 PC06, design §6; FR-PLAN-10): two rubric metrics from runtime telemetry.
-- * New core event RouteBaseline {optimalDistanceMeters} (sent once at session start; the first one counts) and an optional integer
--   steps on Moved. training_event_rejection accepts RouteBaseline like the other core events (payload ranges are checked by the API).
-- * learner_session_finalize (body of 20261028120000_RuntimeRubricMetrics) adds metrics step_count (sum of Moved.steps, null when no
--   Moved event carries steps), optimal_distance_meters (the baseline, shown on the result card, not scorable) and route_efficiency =
--   optimal / actual distance clamped to 0..1 (null without a baseline; 0 with a baseline but no movement). A criterion on route_efficiency or step_count whose
--   value is null is not evaluable (passed null, reason METRIC_NOT_EVALUABLE, weight excluded, never a mandatory failure).
-- * learner_rubric_metrics lists the two metrics with the core ones, so Assessment prepare accepts rubrics that use them.
-- Existing results are immutable and keep their scores.
CREATE TEMP TABLE route_metric_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO route_metric_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION training_event_rejection(p_runtime text,p_type text,p_payload jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE spec jsonb;cat runtime_compatibility_catalog;
BEGIN
 IF p_type IN('ExitReached','WrongExit','HazardExposure','Moved','RouteBaseline') THEN RETURN NULL;END IF;
 -- The same row the editor and the published catalog use for this version (latest active, else latest).
 SELECT * INTO cat FROM runtime_compatibility_catalog WHERE runtime_version=p_runtime ORDER BY is_active DESC,created_at DESC,id LIMIT 1;
 IF cat.id IS NULL THEN RETURN 'EVENT_TYPE_UNSUPPORTED';END IF;
 SELECT ev INTO spec FROM jsonb_each(CASE WHEN jsonb_typeof(cat.capability_contracts)='object' THEN cat.capability_contracts ELSE '{}'::jsonb END) cc(capability,contract)
  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(cc.contract->'events')='array' THEN cc.contract->'events' ELSE '[]'::jsonb END) ev
  WHERE cat.capabilities ? cc.capability AND ev->>'type'=p_type
  ORDER BY cc.capability COLLATE "C" LIMIT 1;
 IF spec IS NULL THEN RETURN 'EVENT_TYPE_UNSUPPORTED';END IF;
 IF NOT capability_payload_valid(spec->'payload',p_payload) THEN RETURN 'EVENT_PAYLOAD_INVALID';END IF;
 RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION learner_rubric_metrics(p_runtime text) RETURNS text[] LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT ARRAY['reached_exit','completion_time_seconds','wrong_exits','hazard_exposure','distance_meters','step_count','route_efficiency']::text[]||ARRAY(SELECT r.metric FROM runtime_event_metrics(p_runtime) r WHERE r.metric<>ALL(ARRAY['reached_exit','completion_time_seconds','wrong_exits','hazard_exposure','distance_meters','step_count','route_efficiency']::text[]) GROUP BY r.metric ORDER BY r.metric COLLATE "C")
$$;

CREATE OR REPLACE FUNCTION learner_session_finalize(p_id uuid) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE s sessions;v scenario_versions;metrics jsonb;c jsonb;results jsonb:='[]';val jsonb;passed boolean;total_w numeric:=0;passed_w numeric:=0;
 mandatory_failed boolean:=false;score numeric;outcome text;reason text;rid uuid;threshold numeric;baseline numeric;distance numeric;
BEGIN
 SELECT * INTO s FROM sessions WHERE id=p_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM session_results WHERE session_id=p_id) THEN RETURN (SELECT id FROM session_results WHERE session_id=p_id); END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=s.scenario_version_id;
 SELECT jsonb_build_object(
   'reached_exit',CASE WHEN bool_or(event_type='ExitReached') THEN 1 ELSE 0 END,
   'completion_time_seconds',round(COALESCE(min(elapsed_ms) FILTER(WHERE event_type='ExitReached'),max(elapsed_ms))/1000.0,3),
   'wrong_exits',count(*) FILTER(WHERE event_type='WrongExit'),
   'hazard_exposure',COALESCE(sum((event_data->>'amount')::numeric) FILTER(WHERE event_type='HazardExposure'),0),
   'distance_meters',COALESCE(sum((event_data->>'distanceMeters')::numeric) FILTER(WHERE event_type='Moved'),0),
   -- BE#216: steps of Moved events, only when the runtime reports them (otherwise NULL, i.e. not evaluable).
   'step_count',CASE WHEN bool_or(event_type='Moved' AND event_data ? 'steps') THEN sum((event_data->>'steps')::numeric) FILTER(WHERE event_type='Moved' AND event_data ? 'steps') END,
   -- BE#216: the first RouteBaseline of the session (shortest safe distance to the assembly point, computed by the runtime).
   'optimal_distance_meters',(array_agg((event_data->>'optimalDistanceMeters')::numeric ORDER BY sequence_number) FILTER(WHERE event_type='RouteBaseline'))[1])
  INTO metrics FROM session_events WHERE session_id=p_id AND sequence_number<=s.completion_last_sequence;
 -- route_efficiency = optimal / actual distance, clamped to 0..1; not evaluable only without a baseline. With a baseline but no
 -- movement it is 0 (the learner did not walk the route), or 1 when the optimal distance itself is 0 (already at the assembly point).
 baseline:=(metrics->>'optimal_distance_meters')::numeric;distance:=(metrics->>'distance_meters')::numeric;
 metrics:=metrics||jsonb_build_object('route_efficiency',CASE WHEN baseline IS NULL THEN NULL WHEN distance>0 THEN least(1,greatest(0,round(baseline/distance,4)))
  WHEN baseline=0 THEN 1 ELSE 0 END);
 -- Player-action counts come from the session's pinned runtime catalog; the core metrics above win over a clashing name.
 metrics:=(SELECT COALESCE(jsonb_object_agg(m.metric,m.n),'{}') FROM (SELECT r.metric,count(e.session_id) n FROM runtime_event_metrics(s.runtime_version) r
  LEFT JOIN session_events e ON e.session_id=p_id AND e.sequence_number<=s.completion_last_sequence AND e.rejection_code IS NULL AND e.event_type=r.event_type
  GROUP BY r.metric) m)||metrics;
 metrics:=jsonb_strip_nulls(metrics);
 IF jsonb_typeof(v.rubric->'criteria')='array' THEN
  FOR c IN SELECT value FROM jsonb_array_elements(v.rubric->'criteria') LOOP
   val:=metrics->(c->>'metric');threshold:=(c->>'threshold')::numeric;
   -- BE#216: route_efficiency and step_count depend on runtime telemetry; without it the criterion is not evaluable: passed null,
   -- reason METRIC_NOT_EVALUABLE, its weight left out and never a mandatory failure. Any other missing metric still fails.
   IF val IS NULL AND c->>'metric' IN('route_efficiency','step_count') THEN
    results:=results||jsonb_build_array(jsonb_build_object('id',c->>'id','metric',c->>'metric','value',NULL,'operator',c->>'operator','threshold',threshold,
     'mandatory',(c->>'mandatory')::boolean,'weight',(c->>'weight')::numeric,'passed',NULL,'reason','METRIC_NOT_EVALUABLE'));
    CONTINUE;
   END IF;
   -- A missing metric is a failed criterion, never unknown.
   passed:=COALESCE(jsonb_typeof(val)='number' AND CASE c->>'operator' WHEN 'gte' THEN val::text::numeric>=threshold WHEN 'lte' THEN val::text::numeric<=threshold WHEN 'eq' THEN val::text::numeric=threshold ELSE false END,false);
   results:=results||jsonb_build_array(jsonb_build_object('id',c->>'id','metric',c->>'metric','value',val,'operator',c->>'operator','threshold',threshold,
    'mandatory',(c->>'mandatory')::boolean,'weight',(c->>'weight')::numeric,'passed',passed,'reason',CASE WHEN val IS NULL THEN 'METRIC_UNAVAILABLE' END));
   total_w:=total_w+(c->>'weight')::numeric;
   IF passed THEN passed_w:=passed_w+(c->>'weight')::numeric;ELSIF (c->>'mandatory')::boolean THEN mandatory_failed:=true;END IF;
  END LOOP;
 END IF;
 score:=CASE WHEN total_w>0 THEN round(100*passed_w/total_w,2) END;
 IF s.mode<>'Assessment' THEN outcome:='NotAssessed';reason:='MODE_NOT_ASSESSED';score:=NULL;
 ELSIF s.completion_end_reason='Abandoned' THEN outcome:='Incomplete';reason:='ENDED_BEFORE_COMPLETION';score:=NULL;
 ELSIF total_w=0 OR v.rubric IS NULL THEN outcome:='NotAssessed';reason:='RUBRIC_NOT_SCORABLE';score:=NULL;
 ELSIF mandatory_failed THEN outcome:='NotPassed';reason:='MANDATORY_CRITERION_FAILED';
 ELSIF passed_w/total_w>=(v.rubric->>'pass_threshold')::numeric THEN outcome:='Passed';reason:='ALL_REQUIREMENTS_MET';
 ELSE outcome:='NotPassed';reason:='BELOW_PASS_THRESHOLD';END IF;
 rid:=gen_random_uuid();
 INSERT INTO session_results(id,session_id,completion_key,last_event_sequence,submission_payload,result_schema_version,rubric_version,score,time_taken_seconds,wrong_exits,
  hazard_exposure_score,total_distance_meters,reached_exit,path_traveled,synced_at,created_at,contract_version,mode,outcome,outcome_reason,rubric_hash,criterion_results,metrics)
 VALUES(rid,s.id,gen_random_uuid(),s.completion_last_sequence,jsonb_build_object('lastEventSequence',s.completion_last_sequence,'endReason',s.completion_end_reason),'7',
  COALESCE(v.rubric->>'schema_version','none'),score,least(round((metrics->>'completion_time_seconds')::numeric),2147483647)::int,least((metrics->>'wrong_exits')::bigint,2147483647)::int,
  round(least((metrics->>'hazard_exposure')::numeric,9999999999.99),2),round(least((metrics->>'distance_meters')::numeric,9999999999.99),2),(metrics->>'reached_exit')::int=1,NULL,clock_timestamp(),clock_timestamp(),
  7,s.mode::text,outcome,reason,s.rubric_hash,CASE WHEN s.mode='Assessment' THEN results END,metrics);
 UPDATE sessions SET status='Completed',ended_at=clock_timestamp() WHERE id=s.id;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),s.trainee_user_id,s.organization_id,'System','Create','SessionResult',rid,s.id,jsonb_build_object('outcome',outcome,'mode',s.mode::text),now());
 RETURN rid;
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM route_metric_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
