-- PlatformAdmin management found missing in the 14/10 Docs review (FR-COMPAT-01, FR-SCENARIO-05, FR-BILLING-01):
-- * runtime catalog versions are created immutable (packages pin runtime_version) and can only be activated/deactivated;
-- * Trial entitlements of one Building are granted, adjusted (units never below used) or cancelled.
-- Every change is audited. The gate owner only receives the columns these gates write.
CREATE TEMP TABLE runtime_trial_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO runtime_trial_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

GRANT INSERT ON runtime_compatibility_catalog TO fet3d_ifc_upload_owner;
GRANT UPDATE(is_active) ON runtime_compatibility_catalog TO fet3d_ifc_upload_owner;
GRANT INSERT ON service_entitlements TO fet3d_ifc_upload_owner;
GRANT UPDATE(status,playtest_units_granted) ON service_entitlements TO fet3d_ifc_upload_owner;
GRANT SELECT ON service_packages TO fet3d_ifc_upload_owner;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='service_packages' AND policyname='admin_service_owner_read') THEN
  CREATE POLICY admin_service_owner_read ON service_packages FOR SELECT TO fet3d_ifc_upload_owner USING(true);
 END IF;
END $$;

CREATE FUNCTION runtime_catalog_representation(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',r.id,'runtimeVersion',r.runtime_version,'protocolVersion',r.protocol_version,'manifestSchemaVersion',r.manifest_schema_version,
  'capabilities',r.capabilities,'capabilityContracts',r.capability_contracts,'isActive',r.is_active,'createdAt',r.created_at)
 FROM runtime_compatibility_catalog r WHERE r.id=p_id
$$;

CREATE FUNCTION runtime_catalog_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;r runtime_compatibility_catalog;new_id uuid;
BEGIN
 IF p_action NOT IN('List','Create','SetActive') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='List' THEN
  RETURN jsonb_build_object('code','OK','result',COALESCE((SELECT jsonb_agg(runtime_catalog_representation(id) ORDER BY created_at DESC,id) FROM runtime_compatibility_catalog),'[]'));
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:runtime-catalog',0));
 IF p_action='Create' THEN
  IF p_input->>'runtimeVersion' !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' OR length(p_input->>'runtimeVersion')>32
   OR NULLIF(btrim(p_input->>'protocolVersion'),'') IS NULL OR length(p_input->>'protocolVersion')>50
   OR NULLIF(btrim(p_input->>'manifestSchemaVersion'),'') IS NULL OR length(p_input->>'manifestSchemaVersion')>50
   OR jsonb_typeof(p_input->'capabilities')<>'array' OR jsonb_typeof(COALESCE(NULLIF(p_input->'capabilityContracts','null'::jsonb),'{}'))<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'capabilities') c WHERE jsonb_typeof(c)<>'string' OR c#>>'{}' !~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$')
   OR (SELECT count(*)<>count(DISTINCT c) FROM jsonb_array_elements_text(p_input->'capabilities') c)
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(COALESCE(NULLIF(p_input->'capabilityContracts','null'::jsonb),'{}')) k WHERE NOT (p_input->'capabilities') ? k)
   THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF EXISTS(SELECT 1 FROM runtime_compatibility_catalog WHERE runtime_version=p_input->>'runtimeVersion') THEN RETURN jsonb_build_object('code','RUNTIME_VERSION_EXISTS','status',409);END IF;
  new_id:=gen_random_uuid();
  INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities,capability_contracts,is_active,created_at)
  VALUES(new_id,p_input->>'runtimeVersion',btrim(p_input->>'protocolVersion'),btrim(p_input->>'manifestSchemaVersion'),p_input->'capabilities',
   COALESCE(NULLIF(p_input->'capabilityContracts','null'::jsonb),'{}'),COALESCE((p_input->>'isActive')::boolean,true),now());
 ELSE
  SELECT * INTO r FROM runtime_compatibility_catalog WHERE id=p_resource FOR UPDATE;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF jsonb_typeof(p_input->'isActive')<>'boolean' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  UPDATE runtime_compatibility_catalog SET is_active=(p_input->>'isActive')::boolean WHERE id=r.id;
  new_id:=r.id;
 END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User',CASE WHEN p_action='Create' THEN 'Create' ELSE 'Update' END::audit_action_enum,'RuntimeCatalog',new_id,gen_random_uuid(),
  jsonb_build_object('operation',p_action,'runtimeVersion',(SELECT runtime_version FROM runtime_compatibility_catalog WHERE id=new_id),'isActive',(SELECT is_active FROM runtime_compatibility_catalog WHERE id=new_id)),now());
 RETURN jsonb_build_object('code','OK','result',runtime_catalog_representation(new_id));
END $$;

CREATE FUNCTION trial_entitlement_representation(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',e.id,'buildingId',e.building_id,'organizationId',e.organization_id,'servicePackageId',e.service_package_id,'status',e.status,
  'startsAt',e.starts_at,'endsAt',e.ends_at,'playtestUnitsGranted',e.playtest_units_granted,'playtestUnitsUsed',e.playtest_units_used,
  'isEffective',e.status='Trial' AND e.starts_at<=now() AND e.ends_at>now(),'createdAt',e.created_at)
 FROM service_entitlements e WHERE e.id=p_id
$$;

CREATE FUNCTION trial_entitlement_gate(p_action text,p_actor uuid,p_family uuid,p_building uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;e service_entitlements;new_id uuid;stamp timestamptz:=now();days integer;units integer;
BEGIN
 IF p_action NOT IN('List','Create','Update') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO b FROM buildings WHERE id=p_building AND deleted_at IS NULL;
 IF b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='List' THEN
  RETURN jsonb_build_object('code','OK','result',COALESCE((SELECT jsonb_agg(trial_entitlement_representation(id) ORDER BY starts_at DESC,id)
   FROM service_entitlements WHERE building_id=b.id AND status IN('Trial','Cancelled') AND payment_transaction_id IS NULL AND quotation_id IS NULL),'[]'));
 END IF;
 -- Same lock as checkout provisioning of this Building, so a Trial never races a paid period.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF p_action='Create' THEN
  days:=(p_input->>'days')::int;units:=(p_input->>'playtestUnits')::int;
  IF days IS NULL OR days NOT BETWEEN 1 AND 90 OR units IS NULL OR units NOT BETWEEN 0 AND 1000 OR p_input->>'servicePackageId' IS NULL
   THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF NOT b.is_active OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF NOT EXISTS(SELECT 1 FROM service_packages WHERE id=(p_input->>'servicePackageId')::uuid) THEN RETURN jsonb_build_object('code','SERVICE_PACKAGE_NOT_FOUND','status',404);END IF;
  IF EXISTS(SELECT 1 FROM service_entitlements WHERE building_id=b.id AND status='Active' AND starts_at<=stamp AND ends_at>stamp) THEN
   RETURN jsonb_build_object('code','BUILDING_SERVICE_ACTIVE','status',409);END IF;
  IF EXISTS(SELECT 1 FROM service_entitlements WHERE building_id=b.id AND status='Trial' AND ends_at>stamp) THEN RETURN jsonb_build_object('code','TRIAL_ALREADY_ACTIVE','status',409);END IF;
  new_id:=gen_random_uuid();
  INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,provisioning_key,status,starts_at,ends_at,playtest_units_granted,created_by)
  VALUES(new_id,b.organization_id,b.id,(p_input->>'servicePackageId')::uuid,'trial:'||new_id,'Trial',stamp,stamp+make_interval(days=>days),units,p_actor);
 ELSE
  SELECT * INTO e FROM service_entitlements WHERE id=(p_input->>'entitlementId')::uuid AND building_id=b.id AND status='Trial' AND payment_transaction_id IS NULL FOR UPDATE;
  IF e.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF COALESCE((p_input->>'cancel')::boolean,false) THEN
   UPDATE service_entitlements SET status='Cancelled' WHERE id=e.id;
  ELSE
   -- The billing guard keeps starts_at/ends_at immutable; a longer Trial is a cancel plus a new grant.
   units:=COALESCE((p_input->>'playtestUnits')::int,e.playtest_units_granted);
   IF units<e.playtest_units_used OR units>1000 THEN RETURN jsonb_build_object('code','TRIAL_UNITS_BELOW_USED','status',409);END IF;
   UPDATE service_entitlements SET playtest_units_granted=units WHERE id=e.id;
  END IF;
  new_id:=e.id;
 END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Create' THEN 'Grant' ELSE 'Update' END::audit_action_enum,'ServiceEntitlement',new_id,gen_random_uuid(),
  jsonb_build_object('operation','Trial'||p_action,'buildingId',b.id)||(SELECT jsonb_build_object('status',status,'endsAt',ends_at,'playtestUnitsGranted',playtest_units_granted) FROM service_entitlements WHERE id=new_id),now());
 RETURN jsonb_build_object('code','OK','result',trial_entitlement_representation(new_id));
END $$;

DO $grants$ DECLARE r text;sig text;s record;BEGIN
 FOREACH sig IN ARRAY ARRAY['runtime_catalog_representation(uuid)','runtime_catalog_gate(text,uuid,uuid,uuid,jsonb)',
   'trial_entitlement_representation(uuid)','trial_entitlement_gate(text,uuid,uuid,uuid,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION runtime_catalog_gate(text,uuid,uuid,uuid,jsonb),trial_entitlement_gate(text,uuid,uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM runtime_trial_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
