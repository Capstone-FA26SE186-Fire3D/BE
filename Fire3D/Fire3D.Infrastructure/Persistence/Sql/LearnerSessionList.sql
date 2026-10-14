-- Trainee training history (issue #78): the caller's own started sessions, newest first. Only v7 rows in sessions are listed,
-- so preparations (training_session_preparations) and Organization playtests (playtest_sessions) never appear. Building
-- access is not rechecked: a Trainee who lost access still sees their own history and results. resultStatus is the stored
-- outcome, or null while no result exists (running, awaiting sync, aborted or crashed), so completion never implies Passed.
CREATE TEMP TABLE learner_session_list_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learner_session_list_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE INDEX IF NOT EXISTS sessions_v7_trainee_started ON sessions(trainee_user_id,started_at DESC,id DESC) WHERE contract_version=7;

CREATE FUNCTION learner_session_list_gate(p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;page integer;size integer;total integer;result jsonb;f_status text;f_mode text;f_building uuid;from_at timestamptz;to_at timestamptz;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) OR (actor.registration_expires_at IS NOT NULL AND actor.email_verified_at IS NULL)
  THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);
 f_status:=p_input->>'status';f_mode:=p_input->>'mode';f_building:=(p_input->>'buildingId')::uuid;
 from_at:=(p_input->>'from')::timestamptz;to_at:=(p_input->>'to')::timestamptz;
 IF page<1 OR size NOT BETWEEN 1 AND 100 OR (from_at IS NOT NULL AND to_at IS NOT NULL AND from_at>to_at)
  OR (f_status IS NOT NULL AND f_status NOT IN('Launching','Running','AwaitingSync','Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable','Aborted','Abandoned','Crashed'))
  OR (f_mode IS NOT NULL AND f_mode NOT IN('Learn','Guided','Assessment'))
  THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 WITH f AS MATERIALIZED(
  SELECT s.id,s.started_at,CASE WHEN s.status='Running' AND s.completion_requested_at IS NOT NULL THEN 'AwaitingSync' ELSE s.status::text END AS state
  FROM sessions s WHERE s.trainee_user_id=p_actor AND s.contract_version=7
   AND (f_mode IS NULL OR s.mode::text=f_mode) AND (f_building IS NULL OR s.building_id=f_building)
   AND (from_at IS NULL OR s.started_at>=from_at) AND (to_at IS NULL OR s.started_at<to_at))
 SELECT (SELECT count(*) FROM f WHERE f_status IS NULL OR state=f_status),
  COALESCE((SELECT jsonb_agg(jsonb_build_object('sessionId',s.id,'status',x.state,'mode',s.mode,'resultStatus',r.outcome,'endReason',s.completion_end_reason,
    'startedAt',s.started_at,'endedAt',s.ended_at,'resultCreatedAt',r.created_at,
    'training',jsonb_build_object('id',t.id,'name',t.name),'releaseId',s.release_id,
    'building',jsonb_build_object('id',b.id,'name',b.name),
    'scenario',jsonb_build_object('id',v.scenario_id,'versionId',v.id,'name',v.name,'versionNumber',v.version_number))
   ORDER BY x.started_at DESC,x.id DESC)
   FROM (SELECT * FROM f WHERE f_status IS NULL OR state=f_status ORDER BY started_at DESC,id DESC OFFSET (page-1)::bigint*size LIMIT size) x
   JOIN sessions s ON s.id=x.id JOIN trainings t ON t.id=s.training_id JOIN buildings b ON b.id=s.building_id
   JOIN scenario_versions v ON v.id=s.scenario_version_id LEFT JOIN session_results r ON r.session_id=s.id),'[]')
 INTO total,result;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page,'pageSize',size));
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION learner_session_list_gate(uuid,uuid,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION learner_session_list_gate(uuid,uuid,jsonb) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION learner_session_list_gate(uuid,uuid,jsonb) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION learner_session_list_gate(uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM learner_session_list_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
