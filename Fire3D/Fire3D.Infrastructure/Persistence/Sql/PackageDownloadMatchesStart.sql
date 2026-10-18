-- Second review (18/10/2026) of RecheckPackageDownloadAccess: before start, a Created playtest accepted any Active/Trial entitlement in
-- its window, while start (PlaytestRuntime.sql) requires an Active entitlement backed by an Applied BuildingService payment of the
-- organization, or a Trial with playtest units left; a missing entitlement was also reported as PACKAGE_SESSION_NOT_ACTIVE instead of
-- start's PLAYTEST_ENTITLEMENT_REQUIRED. The download now uses start's entitlement predicate and code (nothing is consumed), and the
-- actor check also refuses an unverified pending registration, as both start gates do. Learner and started-session rules unchanged.
CREATE TEMP TABLE package_start_match_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO package_start_match_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION runtime_package_files(p_kind text,p_actor uuid,p_family uuid,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;mid uuid;active boolean;m revision_artifacts;prep training_session_preparations;b buildings;ent service_entitlements;
 lim integer;used integer;denied jsonb;stamp timestamptz:=clock_timestamp();ps playtest_sessions;pin playtest_package_pins;rs playtest_runtime_state;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
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
  SELECT s0.* INTO ps FROM playtest_sessions s0 JOIN buildings b1 ON b1.id=s0.building_id WHERE s0.id=p_id AND s0.created_by=p_actor AND b1.organization_id=actor.organization_id;
  SELECT * INTO pin FROM playtest_package_pins WHERE playtest_id=ps.id;
  IF ps.id IS NULL OR pin.playtest_id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  SELECT * INTO rs FROM playtest_runtime_state WHERE playtest_id=ps.id;
  mid:=pin.manifest_artifact_id;
  IF ps.status::text IN('Launching','Running') THEN active:=pin.started_family=p_family;
  ELSIF ps.status::text='Created' AND COALESCE(rs.bound_family,pin.prepared_family)=p_family THEN
   -- The session start would accept (handoff-bound Mobile, else the preparing session), with start's own scope and entitlement rules.
   SELECT * INTO b FROM buildings WHERE id=ps.building_id;
   IF NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
    THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   IF NOT EXISTS(SELECT 1 FROM service_entitlements e WHERE e.organization_id=b.organization_id AND e.building_id=b.id AND e.starts_at<=stamp AND e.ends_at>stamp AND
    ((e.status='Active' AND EXISTS(SELECT 1 FROM payment_transactions tx JOIN payos_payment_requests pr ON pr.id=tx.payment_request_id JOIN quotations q ON q.id=pr.quotation_id
      WHERE tx.id=e.payment_transaction_id AND tx.status='Applied' AND q.billing_purpose='BuildingService' AND q.organization_id=b.organization_id))
     OR (e.status='Trial' AND e.playtest_units_used<e.playtest_units_granted)))
    THEN RETURN jsonb_build_object('code','PLAYTEST_ENTITLEMENT_REQUIRED','status',409);END IF;
   active:=true;
  ELSE active:=false;END IF;
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
 SELECT * INTO s FROM package_start_match_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
