-- Review of BE#120 stack (18/10/2026): AllowPackageDownloadBeforeStart served a Prepared preparation on ownership and expiry only, so
-- a Trainee whose Building access was revoked, whose Training ended, or whose Building had no paid entitlement or no free seat could
-- still download the package for up to 24 hours. A download before start now passes the same checks as start, without allocating:
-- learner_start_recheck (live session, Building/organization active, Public/participation grant, Training/release, content approval,
-- runtime), an Active paid entitlement now, and a seat (the Trainee's existing seat in that entitlement, or capacity left).
-- A Created playtest is served to the session start would accept (COALESCE(bound_family, prepared_family), same as canStart) while its
-- Building and organization are active and the Building has an Active or Trial entitlement now. Started sessions keep their rules.
CREATE TEMP TABLE package_recheck_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO package_recheck_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION runtime_package_files(p_kind text,p_actor uuid,p_family uuid,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;mid uuid;active boolean;m revision_artifacts;prep training_session_preparations;b buildings;ent service_entitlements;
 lim integer;used integer;denied jsonb;stamp timestamptz:=clock_timestamp();
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_kind='Training' THEN
  IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT (s.package->>'manifestArtifactId')::uuid,s.status::text IN('Launching','Running') AND s.completion_requested_at IS NULL AND s.launch_family=p_family INTO mid,active
   FROM sessions s WHERE s.id=p_id AND s.contract_version=7 AND s.trainee_user_id=p_actor;
  IF mid IS NULL THEN
   SELECT * INTO prep FROM training_session_preparations p WHERE p.id=p_id AND p.trainee_user_id=p_actor;
   IF prep.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   IF prep.status<>'Prepared' OR prep.expires_at<=stamp THEN RETURN jsonb_build_object('code','PACKAGE_SESSION_NOT_ACTIVE','status',409);END IF;
   -- Same checks as start, nothing allocated: the download gives no right to start and uses no seat.
   denied:=learner_start_recheck(p_actor,p_family,prep.building_id,prep.training_id,prep.release_id,prep.package,prep.runtime_version);
   IF denied IS NOT NULL THEN RETURN denied;END IF;
   SELECT * INTO b FROM buildings WHERE id=prep.building_id;
   SELECT e2.* INTO ent FROM service_entitlements e2 WHERE e2.building_id=b.id AND e2.organization_id=b.organization_id AND e2.status='Active'
    AND e2.payment_transaction_id IS NOT NULL AND e2.starts_at<=stamp AND e2.ends_at>stamp ORDER BY e2.starts_at DESC,e2.id LIMIT 1;
   IF ent.id IS NULL THEN RETURN jsonb_build_object('code','BUILDING_ENTITLEMENT_REQUIRED','status',409);END IF;
   IF NOT EXISTS(SELECT 1 FROM billing_learner_seats WHERE entitlement_id=ent.id AND trainee_id=p_actor) THEN
    lim:=billing_effective_learner_limit(ent.id,stamp);
    SELECT count(*) INTO used FROM billing_learner_seats WHERE entitlement_id=ent.id;
    IF lim IS NOT NULL AND used>=lim THEN RETURN jsonb_build_object('code','LEARNER_LIMIT_REACHED','status',409);END IF;
   END IF;
   mid:=(prep.package->>'manifestArtifactId')::uuid;active:=true;
  END IF;
 ELSIF p_kind='Playtest' THEN
  IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT pin.manifest_artifact_id,
   ps.status::text IN('Launching','Running') AND pin.started_family=p_family
   OR ps.status::text='Created' AND COALESCE(rs.bound_family,pin.prepared_family)=p_family AND b0.is_active AND b0.deleted_at IS NULL
    AND EXISTS(SELECT 1 FROM organizations o WHERE o.id=b0.organization_id AND o.is_active AND o.deleted_at IS NULL)
    AND EXISTS(SELECT 1 FROM service_entitlements e WHERE e.building_id=b0.id AND e.organization_id=b0.organization_id AND e.status IN('Active','Trial')
     AND e.starts_at<=stamp AND e.ends_at>stamp) INTO mid,active
   FROM playtest_sessions ps JOIN playtest_package_pins pin ON pin.playtest_id=ps.id JOIN buildings b0 ON b0.id=ps.building_id
   LEFT JOIN playtest_runtime_state rs ON rs.playtest_id=ps.id
   WHERE ps.id=p_id AND ps.created_by=p_actor AND b0.organization_id=actor.organization_id;
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
 SELECT * INTO s FROM package_recheck_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
