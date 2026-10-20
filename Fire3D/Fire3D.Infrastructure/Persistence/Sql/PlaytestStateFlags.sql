-- GET /api/playtests/{id} (and the Cancel reply) answered 500 for a playtest cancelled before start: started_family is NULL, so
-- 'launchSession' (p.started_family=p_family) became JSON null and PlaytestStatusResponse.LaunchSession (bool) failed to deserialize.
-- Mobile kept showing the old Created status while /package correctly answered PACKAGE_SESSION_NOT_ACTIVE. Every family flag is now
-- COALESCEd to false; body otherwise unchanged from PlaytestRejectedEvents.sql. Same signature and owner.
CREATE TEMP TABLE playtest_state_flag_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO playtest_state_flag_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
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
  'launchSession',COALESCE(CASE WHEN s.status='Created' THEN COALESCE(r.bound_family,p.prepared_family)=p_family ELSE p.started_family=p_family END,false),
  'openHandoffExpiresAt',(SELECT max(h.expires_at) FROM playtest_handoffs h WHERE h.playtest_id=s.id AND h.redeemed_at IS NULL AND h.revoked_at IS NULL AND h.expires_at>clock_timestamp()),
  -- Rotation revokes older codes, so a tab can compare its own handoffId with the current open one.
  'openHandoffId',(SELECT h.id FROM playtest_handoffs h WHERE h.playtest_id=s.id AND h.redeemed_at IS NULL AND h.revoked_at IS NULL AND h.expires_at>clock_timestamp() ORDER BY h.created_at DESC,h.id LIMIT 1),
  'recovery',jsonb_build_object(
    'canStart',COALESCE(s.status='Created' AND COALESCE(r.bound_family,p.prepared_family)=p_family,false),
    'canReissueGrant',COALESCE(s.status='Launching' AND p.started_family=p_family,false),
    'canSync',COALESCE(s.status='Running' AND p.started_family=p_family,false),
    'canCancel',s.status IN('Created','Launching','Running'),
    -- The handoff gate accepts a new code until start; redeeming it moves the binding to the new Mobile session.
    'canIssueHandoff',s.status='Created'),
  'completion',r.completion,
  -- BE#120: events the pinned runtime does not declare (or whose payload fails its schema) are stored with rejection_code and never
  -- scored; the Web shows them so the author can fix the capability or payload. Newest 20 by sequence.
  'rejectedEvents',jsonb_build_object(
    'total',(SELECT count(*) FROM playtest_events e WHERE e.playtest_id=s.id AND e.rejection_code IS NOT NULL),
    'byCode',jsonb_build_object(
      'EVENT_TYPE_UNSUPPORTED',(SELECT count(*) FROM playtest_events e WHERE e.playtest_id=s.id AND e.rejection_code='EVENT_TYPE_UNSUPPORTED'),
      'EVENT_PAYLOAD_INVALID',(SELECT count(*) FROM playtest_events e WHERE e.playtest_id=s.id AND e.rejection_code='EVENT_PAYLOAD_INVALID')),
    'recent',COALESCE((SELECT jsonb_agg(jsonb_build_object('eventId',x.event_id,'sequence',x.sequence,'type',x.event_type,'code',x.rejection_code,'receivedAt',x.received_at) ORDER BY x.sequence DESC)
      FROM (SELECT e.event_id,e.sequence,e.event_type,e.rejection_code,e.received_at FROM playtest_events e WHERE e.playtest_id=s.id AND e.rejection_code IS NOT NULL
        ORDER BY e.sequence DESC LIMIT 20) x),'[]'::jsonb)))
 FROM playtest_sessions s JOIN playtest_package_pins p ON p.playtest_id=s.id JOIN playtest_runtime_state r ON r.playtest_id=s.id WHERE s.id=p_id
$$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION playtest_state(uuid,uuid) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION playtest_state(uuid,uuid) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION playtest_state(uuid,uuid) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM playtest_state_flag_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
