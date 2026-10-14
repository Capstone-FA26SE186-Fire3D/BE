-- Training analytics v1 contract (issue #80) and corrected definitions from the 14/10 Docs review:
-- * filters buildingId/scenarioId, cohorts by day/week (Monday)/month/building/scenario of session start, UTC [from,to) <= 90 days;
-- * completed excludes Abandoned sessions (abandoned and awaitingSync are reported separately); duration is the runtime's
--   completion_time_seconds, so offline sync delay never inflates it;
-- * debrief aggregates stored criterion results only; unique Trainees are counted inside each group, never summed;
-- * synchronous CSV export of the cohort rows: at most 10,000 rows and 10 successful exports per account per UTC day,
--   audited, no Trainee name/email/ID. Prepare and playtest are never counted (they are not contract-7 sessions).
CREATE TEMP TABLE training_analytics_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO training_analytics_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE TABLE IF NOT EXISTS analytics_exports(
 id uuid PRIMARY KEY,actor_id uuid NOT NULL REFERENCES users(id),organization_id uuid REFERENCES organizations(id),
 report text NOT NULL,filters jsonb NOT NULL CHECK(jsonb_typeof(filters)='object'),row_count integer NOT NULL CHECK(row_count>=0),
 created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS analytics_exports_actor_day ON analytics_exports(actor_id,created_at);
ALTER TABLE analytics_exports ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON analytics_exports FROM PUBLIC;
GRANT SELECT,INSERT ON analytics_exports TO fet3d_ifc_upload_owner;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='analytics_exports' AND policyname='analytics_export_owner') THEN
  CREATE POLICY analytics_export_owner ON analytics_exports TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
 END IF;
END $$;
GRANT SELECT ON scenarios,scenario_versions,trainings,buildings,organizations TO fet3d_ifc_upload_owner;

CREATE FUNCTION analytics_training_report(p_org uuid,p_building uuid,p_scenario uuid,p_from timestamptz,p_to timestamptz,p_as_of timestamptz,
 p_active_seconds integer,p_group text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH cohort AS MATERIALIZED (
  SELECT s.id,s.organization_id,s.training_id,s.building_id,v.scenario_id,s.trainee_user_id,s.mode::text mode,s.started_at,s.status::text status,
   s.completion_requested_at,s.completion_end_reason,r.outcome,r.created_at result_at,r.criterion_results,
   CASE WHEN r.created_at IS NOT NULL AND COALESCE(s.completion_end_reason,'Finished')<>'Abandoned' THEN true ELSE false END completed,
   CASE WHEN r.created_at IS NOT NULL AND s.completion_end_reason='Abandoned' THEN true ELSE false END abandoned,
   CASE WHEN r.created_at IS NULL AND s.completion_requested_at IS NOT NULL AND s.completion_requested_at<=p_as_of THEN true ELSE false END awaiting_sync,
   CASE WHEN r.created_at IS NOT NULL AND COALESCE(s.completion_end_reason,'Finished')<>'Abandoned' AND jsonb_typeof(r.metrics->'completion_time_seconds')='number'
    THEN (r.metrics->>'completion_time_seconds')::numeric END seconds
  FROM sessions s JOIN scenario_versions v ON v.id=s.scenario_version_id
  LEFT JOIN LATERAL (SELECT x.outcome,x.created_at,x.criterion_results,x.metrics FROM session_results x WHERE x.session_id=s.id AND x.contract_version=7 AND x.created_at<=p_as_of
   ORDER BY x.created_at,x.id LIMIT 1) r ON true
  WHERE s.contract_version=7 AND s.started_at>=p_from AND s.started_at<p_to AND s.started_at<=p_as_of
   AND (p_org IS NULL OR s.organization_id=p_org) AND (p_building IS NULL OR s.building_id=p_building) AND (p_scenario IS NULL OR v.scenario_id=p_scenario)),
 grouped AS (
  SELECT CASE p_group WHEN 'day' THEN to_char(started_at AT TIME ZONE 'UTC','YYYY-MM-DD')
    WHEN 'week' THEN to_char(date_trunc('week',started_at AT TIME ZONE 'UTC'),'YYYY-MM-DD')
    WHEN 'month' THEN to_char(started_at AT TIME ZONE 'UTC','YYYY-MM')
    WHEN 'building' THEN building_id::text WHEN 'scenario' THEN scenario_id::text END grp,c.* FROM cohort c WHERE p_group IS NOT NULL),
 rows AS (
  SELECT grp,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(*) FILTER(WHERE completed) completed,count(*) FILTER(WHERE abandoned) abandoned,
   count(*) FILTER(WHERE awaiting_sync) awaiting,count(*) FILTER(WHERE outcome IN('Passed','NotPassed')) assessed,count(*) FILTER(WHERE outcome='Passed') passed,
   round(avg(seconds),1) avg_seconds
  FROM grouped GROUP BY grp),
 criteria AS (
  SELECT e->>'id' criterion_id,e->>'metric' metric,count(*) evaluated,count(*) FILTER(WHERE (e->>'passed')::boolean) passed
  FROM cohort c CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(c.criterion_results)='array' THEN c.criterion_results ELSE '[]' END) e
  WHERE c.mode='Assessment' AND c.outcome IN('Passed','NotPassed') AND jsonb_typeof(e->'passed')='boolean' GROUP BY 1,2)
 SELECT jsonb_build_object(
  'filters',jsonb_build_object('buildingId',p_building,'scenarioId',p_scenario,'groupBy',p_group),
  'plays',(SELECT count(*) FROM cohort),'uniqueTrainees',(SELECT count(DISTINCT trainee_user_id) FROM cohort),
  'completed',(SELECT count(*) FROM cohort WHERE completed),'abandoned',(SELECT count(*) FROM cohort WHERE abandoned),
  'awaitingSync',(SELECT count(*) FROM cohort WHERE awaiting_sync),
  'completionRate',(SELECT CASE WHEN count(*)>0 THEN round((count(*) FILTER(WHERE completed))::numeric/count(*),4) END FROM cohort),
  'activeSessions',(SELECT count(*) FROM sessions s JOIN scenario_versions v ON v.id=s.scenario_version_id WHERE s.contract_version=7 AND s.status='Running'
    AND s.last_heartbeat_at>p_as_of-make_interval(secs=>p_active_seconds) AND s.last_heartbeat_at<=p_as_of AND (p_org IS NULL OR s.organization_id=p_org)
    AND (p_building IS NULL OR s.building_id=p_building) AND (p_scenario IS NULL OR v.scenario_id=p_scenario)),
  'outcomes',(SELECT jsonb_object_agg(o.outcome,(SELECT count(*) FROM cohort c WHERE c.outcome=o.outcome)) FROM unnest(ARRAY['Passed','NotPassed','Incomplete','NotAssessed']) o(outcome)),
  'assessment',(SELECT jsonb_build_object('assessed',count(*) FILTER(WHERE outcome IN('Passed','NotPassed')),'passed',count(*) FILTER(WHERE outcome='Passed'),
    'passRate',CASE WHEN count(*) FILTER(WHERE outcome IN('Passed','NotPassed'))>0 THEN round((count(*) FILTER(WHERE outcome='Passed'))::numeric/count(*) FILTER(WHERE outcome IN('Passed','NotPassed')),4) END) FROM cohort),
  'duration',(SELECT jsonb_build_object('sessions',count(seconds),'averageSeconds',round(avg(seconds),1),
    'medianSeconds',round((percentile_cont(0.5) WITHIN GROUP(ORDER BY seconds))::numeric,1)) FROM cohort WHERE seconds IS NOT NULL),
  'byMode',(SELECT jsonb_agg(jsonb_build_object('mode',m.mode,'plays',(SELECT count(*) FROM cohort c WHERE c.mode=m.mode),'completed',(SELECT count(*) FROM cohort c WHERE c.mode=m.mode AND c.completed)) ORDER BY m.ord)
    FROM unnest(ARRAY['Learn','Guided','Assessment']) WITH ORDINALITY m(mode,ord)),
  'byTraining',COALESCE((SELECT jsonb_agg(jsonb_build_object('trainingId',x.training_id,'trainingName',(SELECT name FROM trainings WHERE id=x.training_id),'plays',x.plays,'completed',x.completed,'passed',x.passed) ORDER BY x.plays DESC,x.training_id) FROM (
    SELECT training_id,count(*) plays,count(*) FILTER(WHERE completed) completed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort GROUP BY training_id ORDER BY count(*) DESC,training_id LIMIT 100) x),'[]'),
  'byBuilding',COALESCE((SELECT jsonb_agg(jsonb_build_object('buildingId',x.building_id,'buildingName',(SELECT name FROM buildings WHERE id=x.building_id),'plays',x.plays,'uniqueTrainees',x.trainees,'completed',x.completed) ORDER BY x.plays DESC,x.building_id) FROM (
    SELECT building_id,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(*) FILTER(WHERE completed) completed FROM cohort GROUP BY building_id ORDER BY count(*) DESC,building_id LIMIT 100) x),'[]'),
  'byScenario',COALESCE((SELECT jsonb_agg(jsonb_build_object('scenarioId',x.scenario_id,'scenarioName',(SELECT name FROM scenarios WHERE id=x.scenario_id),'plays',x.plays,'uniqueTrainees',x.trainees,'completed',x.completed,'passed',x.passed) ORDER BY x.plays DESC,x.scenario_id) FROM (
    SELECT scenario_id,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(*) FILTER(WHERE completed) completed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort GROUP BY scenario_id ORDER BY count(*) DESC,scenario_id LIMIT 100) x),'[]'),
  'byOrganization',CASE WHEN p_org IS NULL THEN COALESCE((SELECT jsonb_agg(jsonb_build_object('organizationId',x.organization_id,'organizationName',(SELECT name FROM organizations WHERE id=x.organization_id),'plays',x.plays,'uniqueTrainees',x.trainees,'completed',x.completed) ORDER BY x.plays DESC,x.organization_id) FROM (
    SELECT organization_id,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(*) FILTER(WHERE completed) completed FROM cohort GROUP BY organization_id ORDER BY count(*) DESC,organization_id LIMIT 100) x),'[]') END,
  'cohorts',CASE WHEN p_group IS NULL THEN NULL ELSE COALESCE((SELECT jsonb_agg(jsonb_build_object('key',r.grp,
    'label',CASE p_group WHEN 'building' THEN (SELECT name FROM buildings WHERE id=r.grp::uuid) WHEN 'scenario' THEN (SELECT name FROM scenarios WHERE id=r.grp::uuid) ELSE r.grp END,
    'plays',r.plays,'uniqueTrainees',r.trainees,'completed',r.completed,'abandoned',r.abandoned,'awaitingSync',r.awaiting,'assessed',r.assessed,'passed',r.passed,
    'passRate',CASE WHEN r.assessed>0 THEN round(r.passed::numeric/r.assessed,4) END,'averageDurationSeconds',r.avg_seconds) ORDER BY r.grp) FROM rows r),'[]') END,
  'debrief',COALESCE((SELECT jsonb_agg(jsonb_build_object('criterionId',criterion_id,'metric',metric,'evaluated',evaluated,'passed',passed,
    'passRate',round(passed::numeric/evaluated,4)) ORDER BY criterion_id,metric) FROM (SELECT * FROM criteria ORDER BY criterion_id,metric LIMIT 100) d),'[]'))
$$;

-- One gate for the three training reports and their exports. Scope: Organization (caller's tenant), Platform (all), PlatformOrganization.
CREATE FUNCTION analytics_training_gate(p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;as_of timestamptz:=transaction_timestamp();scope text:=p_input->>'scope';body jsonb;
 f timestamptz;t timestamptz;building uuid;scenario uuid;grp text:=p_input->>'groupBy';active integer;org_id uuid;rows_count integer;day_start timestamptz;
BEGIN
 IF scope IS NULL OR scope NOT IN('Organization','Platform','PlatformOrganization') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 f:=(p_input->>'from')::timestamptz;t:=(p_input->>'to')::timestamptz;active:=(p_input->>'activeSeconds')::int;
 building:=(p_input->>'buildingId')::uuid;scenario:=(p_input->>'scenarioId')::uuid;
 IF f IS NULL OR t IS NULL OR t<=f OR t-f>interval '90 days' OR active NOT BETWEEN 10 AND 3600 OR (grp IS NOT NULL AND grp NOT IN('day','week','month','building','scenario'))
  THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF scope='Organization' THEN
  IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT * INTO org FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL;
  IF org.id IS NULL THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 ELSE
  IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  IF scope='PlatformOrganization' THEN
   SELECT * INTO org FROM organizations WHERE id=(p_input->>'organizationId')::uuid AND deleted_at IS NULL;
   IF org.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  END IF;
 END IF;
 org_id:=org.id;
 -- Filters stay inside the report's tenant: another tenant's Building or scenario is an unknown resource.
 IF building IS NOT NULL AND NOT EXISTS(SELECT 1 FROM buildings WHERE id=building AND (org_id IS NULL OR organization_id=org_id)) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF scenario IS NOT NULL AND NOT EXISTS(SELECT 1 FROM scenarios WHERE id=scenario AND (org_id IS NULL OR organization_id=org_id) AND (building IS NULL OR building_id=building))
  THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 body:=analytics_training_report(org_id,building,scenario,f,t,as_of,active,CASE WHEN COALESCE((p_input->>'export')::boolean,false) THEN COALESCE(grp,'day') ELSE grp END);
 IF scope<>'Platform' THEN body:=body-'byOrganization';END IF;
 IF COALESCE((p_input->>'export')::boolean,false) THEN
  rows_count:=jsonb_array_length(body->'cohorts');
  IF rows_count>10000 THEN RETURN jsonb_build_object('code','EXPORT_ROW_LIMIT_EXCEEDED','status',422,'rows',rows_count);END IF;
  day_start:=date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:analytics-export:'||p_actor,0));
  IF (SELECT count(*) FROM analytics_exports WHERE actor_id=p_actor AND created_at>=day_start)>=10 THEN
   RETURN jsonb_build_object('code','EXPORT_DAILY_LIMIT_EXCEEDED','status',429,'retryAfterSeconds',ceil(extract(epoch FROM day_start+interval '1 day'-now()))::int);END IF;
  INSERT INTO analytics_exports(id,actor_id,organization_id,report,filters,row_count) VALUES(gen_random_uuid(),p_actor,org_id,'Training'||scope,
   jsonb_build_object('from',f,'to',t,'buildingId',building,'scenarioId',scenario,'groupBy',COALESCE(grp,'day')),rows_count);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,org_id,'User','Download','AnalyticsExport',org_id,gen_random_uuid(),
   jsonb_build_object('report','Training'||scope,'rows',rows_count,'groupBy',COALESCE(grp,'day'),'buildingId',building,'scenarioId',scenario),now());
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('asOf',as_of,'from',f,'to',t)
  ||CASE WHEN org.id IS NOT NULL AND scope='PlatformOrganization' THEN jsonb_build_object('organization',jsonb_build_object('id',org.id,'name',org.name,'isActive',org.is_active)) ELSE '{}'::jsonb END
  ||body||jsonb_build_object('aiUsage',analytics_ai_usage(org_id,f,t,as_of)));
END $$;

DO $grants$ DECLARE r text;sig text;s record;BEGIN
 FOREACH sig IN ARRAY ARRAY['analytics_training_report(uuid,uuid,uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone,integer,text)',
   'analytics_training_gate(uuid,uuid,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION analytics_training_gate(uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON analytics_exports TO fet3d_pending_cleanup_owner;
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='analytics_exports' AND policyname='pending_cleanup_owner') THEN
   CREATE POLICY pending_cleanup_owner ON analytics_exports TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true);
  END IF;
 END IF;
 SELECT * INTO s FROM training_analytics_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
