-- OrganizationUser lists its own organization's AI requests (#58), newest first, with the same tenant representation as
-- GET /api/ai/requests/{id} (no provider payloads or admin-only reconcile fields). Filters: status, operation, mine.
CREATE TEMP TABLE ai_request_list_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_request_list_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION ai_org_request_list_gate(p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;page integer;size integer;total integer;result jsonb;mine boolean;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);mine:=COALESCE((p_input->>'mine')::boolean,false);
 IF page<1 OR size NOT BETWEEN 1 AND 100
  OR (p_input->>'status' IS NOT NULL AND p_input->>'status' NOT IN('Queued','Running','Succeeded','InsufficientEvidence','SafetyRejected','Failed','NeedsReconcile'))
  OR (p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer'))
  THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 WITH f AS MATERIALIZED(SELECT id,created_at FROM ai_requests WHERE organization_id=actor.organization_id
   AND (p_input->>'status' IS NULL OR status=p_input->>'status') AND (p_input->>'operation' IS NULL OR operation=p_input->>'operation')
   AND (NOT mine OR actor_user_id=p_actor))
 SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(ai_request_representation(x.id,false) ORDER BY x.created_at DESC,x.id)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION ai_org_request_list_gate(uuid,uuid,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION ai_org_request_list_gate(uuid,uuid,jsonb) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION ai_org_request_list_gate(uuid,uuid,jsonb) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION ai_org_request_list_gate(uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM ai_request_list_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
