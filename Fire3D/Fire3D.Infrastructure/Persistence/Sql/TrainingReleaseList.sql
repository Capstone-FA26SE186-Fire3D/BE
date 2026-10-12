-- Building Training list with the release state it serves (#53). The runtime login has no SELECT on releases, so the
-- release status and scenario version are added here, after release_access_gate('List') has authorized the actor and
-- filtered the Trainings. The gate lists only Trainings pinned to an Approved content review.
CREATE TEMP TABLE training_release_list_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO training_release_list_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION training_list_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE inner_result jsonb;
BEGIN
 IF p_action<>'List' THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 inner_result:=release_access_gate('List',p_actor,p_family,p_resource,'{}'::jsonb,NULL,NULL);
 IF inner_result->>'code'<>'OK' THEN RETURN inner_result;END IF;
 RETURN jsonb_build_object('code','OK','result',COALESCE((SELECT jsonb_agg(t.item||jsonb_build_object('releaseStatus',r.status::text,'scenarioVersionId',r.scenario_version_id,'reviewStatus','Approved') ORDER BY t.ord)
  FROM jsonb_array_elements(inner_result->'result') WITH ORDINALITY t(item,ord) JOIN releases r ON r.id=(t.item->>'releaseId')::uuid),'[]'::jsonb));
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION training_list_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION training_list_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION training_list_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION training_list_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM training_release_list_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
