-- Forward repair only: preserve existing data, receipts, owner and caller ACLs.
CREATE TEMP TABLE scenario_anchor_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO scenario_anchor_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;
CREATE OR REPLACE FUNCTION scenario_reference_issues(p_revision uuid,p_state jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE issues jsonb:='[]';anchor text;cap text;available jsonb;catalog runtime_compatibility_catalog;
BEGIN
 IF p_state->'objectAnchors' IS NOT NULL AND p_state->'objectAnchors'<>'null'::jsonb THEN
  IF jsonb_typeof(p_state->'objectAnchors') IS DISTINCT FROM 'array' THEN RETURN jsonb_build_array(jsonb_build_object('code','ANCHORS_INVALID','path','$.objectAnchors','message','Anchors must be an array.'));END IF;
  SELECT a.metadata->'objectAnchors' INTO available FROM revision_artifacts a
   JOIN processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
   JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND pa.processing_job_id=j.id AND j.current_attempt_id=pa.id
   WHERE a.revision_id=p_revision AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded'
   AND jsonb_typeof(a.metadata->'objectAnchors')='array'
   AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=j.id AND vr.processing_attempt_id=pa.id
     AND vr.revision_id=a.revision_id AND vr.kind='Geometry' AND vr.outcome='Passed'
     AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')))
   ORDER BY a.created_at DESC,a.id LIMIT 1;
  FOR anchor IN SELECT jsonb_array_elements_text(p_state->'objectAnchors') LOOP
   IF available IS NULL OR NOT available ? anchor THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','ANCHOR_NOT_FOUND','path','$.objectAnchors','message','Anchor does not exist in accepted geometry for this revision.'));END IF;
  END LOOP;
 END IF;
 IF p_state->'requiredCapabilities' IS NOT NULL AND p_state->'requiredCapabilities'<>'null'::jsonb THEN
  IF jsonb_typeof(p_state->'requiredCapabilities') IS DISTINCT FROM 'array' THEN RETURN issues||jsonb_build_array(jsonb_build_object('code','CAPABILITIES_INVALID','path','$.requiredCapabilities','message','Capabilities must be an array.'));END IF;
  IF jsonb_array_length(p_state->'requiredCapabilities')>0 THEN
   SELECT * INTO catalog FROM runtime_compatibility_catalog WHERE runtime_version=p_state->>'runtimeVersion' AND is_active ORDER BY created_at DESC,id LIMIT 1;
   IF catalog.id IS NULL THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','RUNTIME_NOT_SUPPORTED','path','$.runtimeVersion','message','Select an active runtime in the server catalog.'));
   ELSE FOR cap IN SELECT jsonb_array_elements_text(p_state->'requiredCapabilities') LOOP
    IF NOT catalog.capabilities ? cap THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_NOT_SUPPORTED','path','$.requiredCapabilities','message','Runtime does not provide this capability.'));END IF;
   END LOOP;END IF;
  END IF;
 END IF;
 RETURN issues;
END $$;
REVOKE ALL ON FUNCTION scenario_reference_issues(uuid,jsonb) FROM PUBLIC;
DO $$ DECLARE r text;s record;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION scenario_reference_issues(uuid,jsonb) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM scenario_anchor_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
