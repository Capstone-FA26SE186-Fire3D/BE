-- BE#217 (Flow 1 PC06, design §7): an Organization requests a quotation for a Building only once the Building has at least one
-- situation that passed thẩm định. A situation counts when its version has an Approved content review whose content hash matches the
-- version (decisions made by PlatformAdmin before BE#211 keep counting, as the team decided on 24/11/2026). The billing service calls
-- this SECURITY DEFINER function because content reviews are readable only by their owner role.
CREATE TEMP TABLE quote_scenario_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO quote_scenario_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION building_has_approved_scenario(p_building uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT EXISTS(SELECT 1 FROM scenario_versions v JOIN scenario_content_reviews r ON r.scenario_version_id=v.id
  WHERE v.building_id=p_building AND r.status='Approved' AND r.content_hash=v.scenario_hash)
$$;

DO $grants$ DECLARE s record;r text;BEGIN
 GRANT SELECT ON scenario_versions,scenario_content_reviews TO fet3d_ifc_upload_owner;
 ALTER FUNCTION building_has_approved_scenario(uuid) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION building_has_approved_scenario(uuid) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION building_has_approved_scenario(uuid) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION building_has_approved_scenario(uuid) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM quote_scenario_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
