-- Library contract (issue #88): admin list filters isActive and code search; publishing a version requires every required
-- capability to be offered by an active runtime catalog row. Code and kind stay immutable (business key and payload schema).
-- Signature unchanged, so owner and EXECUTE grants stay as they are.
CREATE TEMP TABLE library_gap_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO library_gap_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION library_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;i organization_library_items;v organization_library_versions;receipt content_command_receipts;h text;result jsonb;n integer;stamp timestamptz:=clock_timestamp();
 page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);total integer;admin boolean;search text:=catalog_search_pattern(upper(p_input->>'search'));
BEGIN
 IF p_action NOT IN('List','Get','GetVersion','CreateItem','UpdateItem','CreateVersion','UpdateVersion','PublishVersion') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role NOT IN('PlatformAdmin','OrganizationUser') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF actor.role='OrganizationUser' AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 admin:=actor.role='PlatformAdmin' AND COALESCE((p_input->>'admin')::boolean,false);
 IF p_action IN('List','Get','GetVersion') THEN
  IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF COALESCE((p_input->>'admin')::boolean,false) AND actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  IF p_action='List' THEN
   -- Organizations see active items with a published version; inactive items stay out of new selections.
   SELECT count(*) INTO total FROM organization_library_items x WHERE (p_input->>'kind' IS NULL OR x.kind=p_input->>'kind') AND (admin OR (x.is_active AND EXISTS(SELECT 1 FROM organization_library_versions WHERE item_id=x.id AND status='Published')))
    AND (p_input->>'isActive' IS NULL OR x.is_active=(p_input->>'isActive')::boolean) AND (search IS NULL OR x.code ILIKE search ESCAPE '\');
   SELECT COALESCE(jsonb_agg(library_item_representation(y.id,admin) ORDER BY y.code),'[]') INTO result FROM (SELECT x.id,x.code FROM organization_library_items x
    WHERE (p_input->>'kind' IS NULL OR x.kind=p_input->>'kind') AND (admin OR (x.is_active AND EXISTS(SELECT 1 FROM organization_library_versions WHERE item_id=x.id AND status='Published')))
    AND (p_input->>'isActive' IS NULL OR x.is_active=(p_input->>'isActive')::boolean) AND (search IS NULL OR x.code ILIKE search ESCAPE '\')
    ORDER BY x.code OFFSET (page-1)::bigint*size LIMIT size) y;
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
  ELSIF p_action='Get' THEN
   SELECT * INTO i FROM organization_library_items WHERE id=p_resource;
   IF i.id IS NULL OR (NOT admin AND (NOT i.is_active OR NOT EXISTS(SELECT 1 FROM organization_library_versions WHERE item_id=i.id AND status='Published'))) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   RETURN jsonb_build_object('code','OK','result',library_item_representation(i.id,admin));
  ELSE
   -- A published version stays readable after deactivation so referencing snapshots can be inspected.
   SELECT * INTO v FROM organization_library_versions WHERE id=p_resource;
   IF v.id IS NULL OR (NOT admin AND v.status<>'Published') THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   RETURN jsonb_build_object('code','OK','result',library_version_representation(v.id));
  END IF;
 END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action IN('CreateItem','CreateVersion') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  PERFORM pg_advisory_xact_lock(hashtextextended('content:'||p_actor||':library'||p_action||':'||p_key,0));
  SELECT * INTO receipt FROM content_command_receipts WHERE actor_id=p_actor AND operation='Library'||p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result);
  END IF;
 ELSIF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
 IF p_action='CreateItem' THEN
  IF p_input->>'kind' NOT IN('ScenarioTemplate','RubricSample','Equipment') OR p_input->>'code' !~ '^[A-Z0-9][A-Z0-9_-]{0,79}$' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:library-code:'||(p_input->>'code'),0));
  IF EXISTS(SELECT 1 FROM organization_library_items WHERE code=p_input->>'code') THEN RETURN jsonb_build_object('code','LIBRARY_CODE_EXISTS','status',409);END IF;
  INSERT INTO organization_library_items(id,kind,code,created_by,created_at,updated_at) VALUES(gen_random_uuid(),p_input->>'kind',p_input->>'code',p_actor,stamp,stamp) RETURNING * INTO i;
 ELSIF p_action='UpdateItem' THEN
  SELECT * INTO i FROM organization_library_items WHERE id=p_resource FOR UPDATE;
  IF i.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF i.revision<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF jsonb_typeof(p_input->'isActive') IS DISTINCT FROM 'boolean' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  UPDATE organization_library_items SET is_active=(p_input->>'isActive')::boolean,revision=revision+1,updated_at=stamp WHERE id=i.id RETURNING * INTO i;
 ELSIF p_action='CreateVersion' THEN
  SELECT * INTO i FROM organization_library_items WHERE id=p_resource FOR UPDATE;
  IF i.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  SELECT COALESCE(max(version_number),0)+1 INTO n FROM organization_library_versions WHERE item_id=i.id;
  INSERT INTO organization_library_versions(id,item_id,version_number,name,payload,required_capabilities,payload_hash,status,created_by,created_at,updated_at)
  VALUES(gen_random_uuid(),i.id,n,btrim(p_input->>'name'),p_input->'payload',COALESCE(p_input->'requiredCapabilities','[]'),fet3d_jsonb_payload_hash(p_input->'payload'),'Draft',p_actor,stamp,stamp) RETURNING * INTO v;
 ELSE
  SELECT * INTO v FROM organization_library_versions WHERE id=p_resource FOR UPDATE;
  IF v.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  SELECT * INTO i FROM organization_library_items WHERE id=v.item_id FOR UPDATE;
  IF v.revision<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF v.status<>'Draft' THEN RETURN jsonb_build_object('code','LIBRARY_VERSION_PUBLISHED','status',409);END IF;
  IF p_action='UpdateVersion' THEN
   UPDATE organization_library_versions SET name=btrim(p_input->>'name'),payload=p_input->'payload',required_capabilities=COALESCE(p_input->'requiredCapabilities','[]'),
    payload_hash=fet3d_jsonb_payload_hash(p_input->'payload'),revision=revision+1,updated_at=stamp WHERE id=v.id RETURNING * INTO v;
  ELSE
   -- Templates and equipment may only require capabilities some active runtime supports (FR-LIBRARY-02).
   IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(v.required_capabilities) c
    WHERE NOT EXISTS(SELECT 1 FROM runtime_compatibility_catalog r WHERE r.is_active AND jsonb_typeof(r.capabilities)='array' AND r.capabilities ? c)) THEN
    RETURN jsonb_build_object('code','LIBRARY_CAPABILITY_UNSUPPORTED','status',409);END IF;
   UPDATE organization_library_versions SET status='Published',published_by=p_actor,published_at=stamp,revision=revision+1,updated_at=stamp WHERE id=v.id RETURNING * INTO v;
   -- latestPublished is part of the item representation, so the item revision moves too and keys the invalidation.
   UPDATE organization_library_items SET revision=revision+1,updated_at=stamp WHERE id=i.id RETURNING * INTO i;
   PERFORM content_invalidate('library',i.id,i.revision,jsonb_build_object('versionId',v.id,'kind',i.kind));
  END IF;
 END IF;
 IF p_action='UpdateItem' THEN PERFORM content_invalidate('library',i.id,i.revision,jsonb_build_object('isActive',i.is_active,'kind',i.kind));END IF;
 result:=CASE WHEN p_action IN('CreateItem','UpdateItem') THEN library_item_representation(i.id,true) ELSE library_version_representation(v.id) END;
 IF p_action IN('CreateItem','CreateVersion') THEN INSERT INTO content_command_receipts VALUES(p_actor,'Library'||p_action,p_key,h,result,stamp);END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User','Update',CASE WHEN p_action IN('CreateItem','UpdateItem') THEN 'LibraryItem' ELSE 'LibraryVersion' END,COALESCE(v.id,i.id),gen_random_uuid(),
  jsonb_build_object('operation',p_action,'itemId',i.id,'kind',i.kind,'payloadHash',v.payload_hash),now());
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM library_gap_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
