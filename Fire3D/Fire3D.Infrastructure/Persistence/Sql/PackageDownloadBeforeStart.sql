-- Docs FR-TRAINING-04 (requirements:133, technology:7/257): prepare pins the Training/package and Mobile downloads and verifies the
-- package BEFORE start; only start checks access, entitlement and the learner seat. runtime_package_files (AddPackageBuildPull) only
-- served started sessions, so a Trainee would consume a seat before knowing the package downloads. Now it also serves:
-- - a learner preparation that is Prepared and not expired, to its Trainee (same id as the later session);
-- - a playtest still Created, to the Mobile login that redeemed the handoff (bound_family), before start.
-- Started sessions keep the previous rule (launching login, Launching/Running, not completing). Same signature, owner and grants.
CREATE TEMP TABLE package_download_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO package_download_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION runtime_package_files(p_kind text,p_actor uuid,p_family uuid,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;mid uuid;active boolean;m revision_artifacts;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_kind='Training' THEN
  IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT (s.package->>'manifestArtifactId')::uuid,s.status::text IN('Launching','Running') AND s.completion_requested_at IS NULL AND s.launch_family=p_family INTO mid,active
   FROM sessions s WHERE s.id=p_id AND s.contract_version=7 AND s.trainee_user_id=p_actor;
  IF mid IS NULL THEN
   -- Before start: the preparation pins the package; downloading it grants nothing and uses no seat.
   SELECT (p.package->>'manifestArtifactId')::uuid,p.status='Prepared' AND p.expires_at>clock_timestamp() INTO mid,active
    FROM training_session_preparations p WHERE p.id=p_id AND p.trainee_user_id=p_actor;
  END IF;
 ELSIF p_kind='Playtest' THEN
  IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT pin.manifest_artifact_id,
   ps.status::text IN('Launching','Running') AND pin.started_family=p_family OR ps.status::text='Created' AND rs.bound_family=p_family INTO mid,active
   FROM playtest_sessions ps JOIN playtest_package_pins pin ON pin.playtest_id=ps.id JOIN buildings b ON b.id=ps.building_id
   LEFT JOIN playtest_runtime_state rs ON rs.playtest_id=ps.id
   WHERE ps.id=p_id AND ps.created_by=p_actor AND b.organization_id=actor.organization_id;
 ELSE RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 IF mid IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF NOT COALESCE(active,false) THEN RETURN jsonb_build_object('code','PACKAGE_SESSION_NOT_ACTIVE','status',409);END IF;
 SELECT * INTO m FROM revision_artifacts WHERE id=mid AND artifact_type='manifest' AND is_runtime_ready;
 IF m.id IS NULL OR m.schema_version IS DISTINCT FROM 'fet3d.package/1' OR jsonb_typeof(m.metadata->'files') IS DISTINCT FROM 'array' THEN
  RETURN jsonb_build_object('code','PACKAGE_FORMAT_UNSUPPORTED','status',409);
 END IF;
 RETURN jsonb_build_object('code','OK','manifestKey',m.object_key,'manifestSha256',m.sha256_hash,'manifestSizeBytes',m.size_bytes,
  'prefix',left(m.object_key,length(m.object_key)-length('manifest.json')),'files',m.metadata->'files','floors',m.metadata->'floors',
  'runtimeVersion',m.metadata->>'runtimeVersion','buildTarget',m.metadata->>'buildTarget');
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION runtime_package_files(text,uuid,uuid,uuid) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION runtime_package_files(text,uuid,uuid,uuid) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION runtime_package_files(text,uuid,uuid,uuid) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION runtime_package_files(text,uuid,uuid,uuid) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM package_download_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
