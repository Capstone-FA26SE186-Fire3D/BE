-- BE#224: door.exit_locked (exit door locked) and door.fire_wedged (fire door wedged open) are the state of an existing IFC door in one
-- situation, like BlockedElement, not fixed equipment. They join item.towel and bait.belongings as scenario-only capabilities:
-- a draft on a fire infrastructure may hold them (no SCENARIO_CONTAINS_INFRASTRUCTURE) and the infrastructure refuses them in the API
-- (INFRASTRUCTURE_CAPABILITY_NOT_FIXED). scenario_infrastructure_conflicts keeps the body of 20261124110000_FireInfrastructure with
-- only that list and the message changed; CREATE OR REPLACE keeps owner and grants. No stored infrastructure held them (checked).
CREATE TEMP TABLE door_state_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO door_state_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION scenario_infrastructure_conflicts(p_state jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT COALESCE(jsonb_agg(jsonb_build_object('code','SCENARIO_CONTAINS_INFRASTRUCTURE','path','$.objects['||(e.n-1)||']',
   'message',CASE WHEN e.o->>'kind'='Goal' THEN 'Exits and assembly points come from the fire infrastructure; remove this Goal.'
    ELSE 'Fixed equipment comes from the fire infrastructure; a scenario may only place item.towel, bait.belongings, door.exit_locked and door.fire_wedged.' END) ORDER BY e.n),'[]'::jsonb)
 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_state->'objects')='array' THEN p_state->'objects' ELSE '[]'::jsonb END) WITH ORDINALITY e(o,n)
 WHERE jsonb_typeof(e.o)='object' AND (e.o->>'kind'='Goal' OR (e.o->>'kind'='Equipment'
  AND COALESCE(e.o->'capability'->>'id','') NOT IN('item.towel','bait.belongings','door.exit_locked','door.fire_wedged')))
$$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM door_state_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
