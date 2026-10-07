-- Forward repair: a non-superuser migration identity cannot GRANT on functions owned by another role.
CREATE TEMP TABLE ifc_dependency_grant_state(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_integration_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_integration_owner','SET');INSERT INTO ifc_dependency_grant_state VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_integration_owner TO %I WITH SET TRUE, INHERIT FALSE',current_user);END IF;
END $$;
SET LOCAL ROLE fet3d_integration_owner;
GRANT EXECUTE ON FUNCTION fet3d_jsonb_payload_hash(jsonb),enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb),enqueue_integration_outbox_event_internal(text,text,uuid,text,text,jsonb,boolean,boolean) TO fet3d_ifc_upload_owner;
RESET ROLE;
DO $$ DECLARE s ifc_dependency_grant_state;BEGIN
 SELECT * INTO s FROM ifc_dependency_grant_state;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_integration_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_integration_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
 IF NOT has_function_privilege('fet3d_ifc_upload_owner','fet3d_jsonb_payload_hash(jsonb)','EXECUTE') OR NOT has_function_privilege('fet3d_ifc_upload_owner','enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb)','EXECUTE') OR NOT has_function_privilege('fet3d_ifc_upload_owner','enqueue_integration_outbox_event_internal(text,text,uuid,text,text,jsonb,boolean,boolean)','EXECUTE') THEN RAISE EXCEPTION 'IFC dependency grants missing';END IF;
END $$;
