-- Playtest reads for the Web (issue #85): the caller's playtests of a scenario (recover after reload or on another device),
-- an advisory availability check before preparing, and canIssueHandoff/openHandoffId in the playtest state.
CREATE TEMP TABLE playtest_read_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO playtest_read_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION playtest_state(p_id uuid,p_family uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',s.id,'buildingId',s.building_id,'revisionId',s.revision_id,'scenarioVersionId',s.scenario_version_id,
  'status',s.status,'createdAt',s.created_at,'startedAt',s.started_at,'endedAt',s.ended_at,'runtimeVersion',s.runtime_version,
  'package',jsonb_build_object('artifactId',p.artifact_id,'manifestArtifactId',p.manifest_artifact_id,'validationRunId',p.validation_run_id,
    'packageHash',p.snapshot->>'packageHash','manifestHash',p.snapshot->>'manifestHash','buildTarget',p.snapshot->>'buildTarget',
    'protocolVersion',s.protocol_version,'manifestSchemaVersion',s.manifest_schema_version),
  'grant',CASE WHEN r.grant_generation>0 THEN jsonb_build_object('generation',r.grant_generation,'issuedAt',r.grant_issued_at,'expiresAt',r.grant_expires_at) END,
  'launchedAt',r.launched_at,'lastHeartbeatAt',r.last_heartbeat_at,
  'acknowledgedSequence',COALESCE((SELECT max(sequence) FROM (SELECT sequence,row_number() OVER(ORDER BY sequence) n FROM playtest_events WHERE playtest_id=s.id) x WHERE sequence=n),0),
  'boundToMobile',r.bound_family IS NOT NULL,
  'launchSession',CASE WHEN s.status='Created' THEN COALESCE(r.bound_family,p.prepared_family)=p_family ELSE p.started_family=p_family END,
  'openHandoffExpiresAt',(SELECT max(h.expires_at) FROM playtest_handoffs h WHERE h.playtest_id=s.id AND h.redeemed_at IS NULL AND h.revoked_at IS NULL AND h.expires_at>clock_timestamp()),
  -- Rotation revokes older codes, so a tab can compare its own handoffId with the current open one.
  'openHandoffId',(SELECT h.id FROM playtest_handoffs h WHERE h.playtest_id=s.id AND h.redeemed_at IS NULL AND h.revoked_at IS NULL AND h.expires_at>clock_timestamp() ORDER BY h.created_at DESC,h.id LIMIT 1),
  'recovery',jsonb_build_object(
    'canStart',s.status='Created' AND COALESCE(r.bound_family,p.prepared_family)=p_family,
    'canReissueGrant',s.status='Launching' AND p.started_family=p_family,
    'canSync',s.status='Running' AND p.started_family=p_family,
    'canCancel',s.status IN('Created','Launching','Running'),
    -- The handoff gate accepts a new code until start; redeeming it moves the binding to the new Mobile session.
    'canIssueHandoff',s.status='Created'),
  'completion',r.completion)
 FROM playtest_sessions s JOIN playtest_package_pins p ON p.playtest_id=s.id JOIN playtest_runtime_state r ON r.playtest_id=s.id WHERE s.id=p_id
$$;

CREATE FUNCTION playtest_read_gate(p_action text,p_actor uuid,p_family uuid,p_scenario uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;s scenarios;b buildings;v scenario_versions;page integer;size integer;total integer;result jsonb;f_status text;
 stamp timestamptz:=clock_timestamp();paid service_entitlements;trial service_entitlements;package_ready boolean;blockers jsonb:='[]';
BEGIN
 IF p_action NOT IN('List','Availability') THEN RETURN jsonb_build_object('code','PLAYTEST_ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO s FROM scenarios WHERE id=p_scenario;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 IF s.id IS NULL OR b.id IS NULL OR b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_input->>'scenarioVersionId' IS NOT NULL THEN
  SELECT * INTO v FROM scenario_versions WHERE id=(p_input->>'scenarioVersionId')::uuid AND scenario_id=s.id;
  IF v.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 END IF;
 IF p_action='List' THEN
  -- Only the caller's own playtests: every other playtest route is creator-scoped too.
  page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);f_status:=p_input->>'status';
  IF page<1 OR size NOT BETWEEN 1 AND 100 OR (f_status IS NOT NULL AND f_status NOT IN('Created','Launching','Running','Completed','Cancelled'))
   THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  WITH f AS MATERIALIZED(SELECT ps.id,ps.created_at FROM playtest_sessions ps JOIN scenario_versions sv ON sv.id=ps.scenario_version_id
    WHERE sv.scenario_id=s.id AND ps.created_by=p_actor AND (v.id IS NULL OR ps.scenario_version_id=v.id) AND (f_status IS NULL OR ps.status::text=f_status))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(jsonb_build_object('id',ps.id,'scenarioVersionId',ps.scenario_version_id,'versionNumber',sv.version_number,
    'revisionId',ps.revision_id,'status',ps.status,'createdAt',ps.created_at,'startedAt',ps.started_at,'endedAt',ps.ended_at,
    'boundToMobile',COALESCE(r.bound_family IS NOT NULL,false)) ORDER BY x.created_at DESC,x.id DESC)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id DESC OFFSET (page-1)::bigint*size LIMIT size) x JOIN playtest_sessions ps ON ps.id=x.id
   JOIN scenario_versions sv ON sv.id=ps.scenario_version_id LEFT JOIN playtest_runtime_state r ON r.playtest_id=ps.id),'[]') INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 END IF;
 -- Availability mirrors start: a paid BuildingService entitlement in its window, otherwise a Trial with playtest units left.
 -- It is advisory only; start still rechecks everything under its locks.
 SELECT e.* INTO paid FROM service_entitlements e WHERE e.organization_id=b.organization_id AND e.building_id=b.id AND e.status='Active' AND e.starts_at<=stamp AND e.ends_at>stamp
  AND EXISTS(SELECT 1 FROM payment_transactions tx JOIN payos_payment_requests pr ON pr.id=tx.payment_request_id JOIN quotations q ON q.id=pr.quotation_id
   WHERE tx.id=e.payment_transaction_id AND tx.status='Applied' AND q.billing_purpose='BuildingService' AND q.organization_id=b.organization_id)
  ORDER BY e.ends_at DESC,e.id LIMIT 1;
 SELECT e.* INTO trial FROM service_entitlements e WHERE e.organization_id=b.organization_id AND e.building_id=b.id AND e.status='Trial' AND e.starts_at<=stamp AND e.ends_at>stamp
  ORDER BY e.ends_at DESC,e.id LIMIT 1;
 IF paid.id IS NULL AND (trial.id IS NULL OR trial.playtest_units_used>=trial.playtest_units_granted) THEN blockers:=blockers||'"PLAYTEST_ENTITLEMENT_REQUIRED"'::jsonb;END IF;
 IF v.id IS NOT NULL THEN
  IF v.state_snapshot IS NULL THEN package_ready:=false;
  ELSE package_ready:=EXISTS(SELECT 1 FROM revision_artifacts art JOIN validation_runs run ON run.candidate_artifact_id=art.id
   JOIN processing_jobs j ON j.id=art.job_id AND j.current_attempt_id=art.attempt_id JOIN processing_job_attempts pa ON pa.id=art.attempt_id
   WHERE j.scenario_version_id=v.id AND j.revision_id=v.revision_id AND j.kind='PlaytestPackage' AND j.status='Succeeded' AND pa.status='Succeeded'
   AND art.artifact_type='unity_package' AND art.is_runtime_ready AND run.processing_attempt_id=pa.id AND run.scenario_version_id=v.id AND run.scenario_hash=v.scenario_hash AND run.outcome='Passed'
   AND NOT EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical'))
   AND EXISTS(SELECT 1 FROM revision_artifacts m WHERE m.job_id=art.job_id AND m.attempt_id=art.attempt_id AND m.artifact_type='manifest' AND m.is_runtime_ready));
  END IF;
  IF NOT package_ready THEN blockers:=blockers||'"PLAYTEST_PACKAGE_REQUIRED"'::jsonb;END IF;
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object(
  'entitlement',jsonb_build_object('kind',CASE WHEN paid.id IS NOT NULL THEN 'Paid' WHEN trial.id IS NOT NULL THEN 'Trial' ELSE 'None' END,
   'endsAt',COALESCE(paid.ends_at,trial.ends_at),
   'trialUnitsRemaining',CASE WHEN trial.id IS NULL THEN NULL ELSE greatest(trial.playtest_units_granted-trial.playtest_units_used,0) END),
  'scenarioVersionId',v.id,'packageReady',package_ready,'blockers',blockers));
END $$;


DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION playtest_read_gate(text,uuid,uuid,uuid,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION playtest_read_gate(text,uuid,uuid,uuid,jsonb) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION playtest_read_gate(text,uuid,uuid,uuid,jsonb) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION playtest_read_gate(text,uuid,uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM playtest_read_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
