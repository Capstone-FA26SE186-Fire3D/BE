-- STYLE-01 (Docs ifc_ingestion_visual_interaction.md 5.5): material style per revision.
-- * PlatformAdmin keeps style presets (per building type: floor/wall/ceiling materials by default and by room category) and colour
--   schemes (wall, accent wall, door wood, skirting) as Organization Library kinds StylePreset and ColorScheme, so they get the
--   Library's versions, publish, audit and cache invalidation. library_gate is the one from AddLibraryContractGaps with only the
--   accepted kinds extended.
-- * An OrganizationUser picks a published preset, optionally a scheme, and material overrides by room/surface for a revision.
--   Selections are an append-only, ETag'd overlay (revision_style_selections): a later change never rewrites an earlier version,
--   so a build that pinned a selection version keeps it. Workers read the selection when they build (not yet wired).
CREATE TEMP TABLE revision_style_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO revision_style_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

ALTER TABLE organization_library_items DROP CONSTRAINT organization_library_items_kind_check;
ALTER TABLE organization_library_items ADD CONSTRAINT organization_library_items_kind_check
 CHECK(kind IN('ScenarioTemplate','RubricSample','Equipment','StylePreset','ColorScheme'));

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
  IF p_input->>'kind' NOT IN('ScenarioTemplate','RubricSample','Equipment','StylePreset','ColorScheme') OR p_input->>'code' !~ '^[A-Z0-9][A-Z0-9_-]{0,79}$' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
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

CREATE TABLE revision_style_selections(
 id uuid PRIMARY KEY,revision_id uuid NOT NULL REFERENCES revisions(id),version_number integer NOT NULL CHECK(version_number>0),
 data jsonb NOT NULL CHECK(jsonb_typeof(data)='object'),created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,
 UNIQUE(revision_id,version_number));
CREATE FUNCTION immutable_revision_style_selection() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Revision style selections are append-only';END $$;
CREATE TRIGGER immutable_revision_style_selection BEFORE UPDATE OR DELETE ON revision_style_selections FOR EACH ROW EXECUTE FUNCTION immutable_revision_style_selection();

-- A published version of an active Library item of the given kind, as the selection shows it.
CREATE FUNCTION revision_style_library_ref(p_version uuid,p_kind text) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('versionId',v.id,'itemId',i.id,'code',i.code,'name',v.name,'versionNumber',v.version_number,'payload',v.payload)
 FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id
 WHERE v.id=p_version AND i.kind=p_kind AND v.status='Published' AND i.is_active
$$;

-- Get: the latest selection (null before the first save) and the newest earlier revision's selection of the same Building as a
-- suggestion. Save: If-Match on the selection version; preset and scheme must be published versions of active items of their kind.
CREATE FUNCTION revision_style_gate(p_action text,p_actor uuid,p_family uuid,p_revision uuid,p_input jsonb,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;rev revisions;cur integer;sel revision_style_selections;prev revision_style_selections;issues jsonb:='[]';
 preset jsonb;scheme jsonb;stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Get','Save') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT r.* INTO rev FROM revisions r JOIN buildings b ON b.id=r.building_id JOIN organizations o ON o.id=b.organization_id
  WHERE r.id=p_revision AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
   AND (actor.role='PlatformAdmin' OR b.organization_id=actor.organization_id);
 IF rev.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='Save' THEN PERFORM 1 FROM revisions WHERE id=rev.id FOR UPDATE;END IF;
 SELECT COALESCE(max(version_number),0) INTO cur FROM revision_style_selections WHERE revision_id=rev.id;

 IF p_action='Save' THEN
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  IF p_expected<>cur THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF rev.status::text IN('Superseded','Rejected') THEN RETURN jsonb_build_object('code','REVISION_NOT_EDITABLE','status',409);END IF;
  preset:=revision_style_library_ref(NULLIF(p_input->>'presetVersionId','')::uuid,'StylePreset');
  IF preset IS NULL THEN
   issues:=issues||jsonb_build_array(jsonb_build_object('code','STYLE_PRESET_UNAVAILABLE','path','$.presetVersionId','message','Use a published version of an active StylePreset.'));
  END IF;
  IF NULLIF(p_input->>'schemeVersionId','') IS NOT NULL AND revision_style_library_ref((p_input->>'schemeVersionId')::uuid,'ColorScheme') IS NULL THEN
   issues:=issues||jsonb_build_array(jsonb_build_object('code','COLOR_SCHEME_UNAVAILABLE','path','$.schemeVersionId','message','Use a published version of an active ColorScheme.'));
  END IF;
  IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','STYLE_SELECTION_INVALID','status',422,'issues',issues);END IF;
  cur:=cur+1;
  INSERT INTO revision_style_selections(id,revision_id,version_number,data,created_by,created_at)
  VALUES(gen_random_uuid(),rev.id,cur,jsonb_build_object('presetVersionId',p_input->>'presetVersionId','schemeVersionId',NULLIF(p_input->>'schemeVersionId',''),
   'overrides',CASE WHEN jsonb_typeof(p_input->'overrides')='array' THEN p_input->'overrides' ELSE '[]'::jsonb END),p_actor,stamp);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,(SELECT b.organization_id FROM buildings b WHERE b.id=rev.building_id),'User','Update','RevisionStyle',rev.id,gen_random_uuid(),
   jsonb_build_object('version',cur,'presetVersionId',p_input->>'presetVersionId','schemeVersionId',NULLIF(p_input->>'schemeVersionId','')),now());
 END IF;

 SELECT * INTO sel FROM revision_style_selections WHERE revision_id=rev.id ORDER BY version_number DESC LIMIT 1;
 SELECT s.* INTO prev FROM revision_style_selections s JOIN revisions r2 ON r2.id=s.revision_id
  WHERE r2.building_id=rev.building_id AND r2.id<>rev.id AND r2.created_at<=rev.created_at ORDER BY r2.created_at DESC,s.version_number DESC LIMIT 1;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('revisionId',rev.id,'version',cur,
  'selection',CASE WHEN sel.id IS NULL THEN NULL ELSE jsonb_build_object(
    -- A preset or scheme withdrawn later still shows what was chosen (by id) so the stored selection stays explainable.
    'preset',COALESCE((SELECT jsonb_build_object('versionId',v.id,'itemId',i.id,'code',i.code,'name',v.name,'versionNumber',v.version_number,'payload',v.payload)
      FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id WHERE v.id=(sel.data->>'presetVersionId')::uuid),'null'::jsonb),
    'scheme',(SELECT jsonb_build_object('versionId',v.id,'itemId',i.id,'code',i.code,'name',v.name,'versionNumber',v.version_number,'payload',v.payload)
      FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id WHERE v.id=(sel.data->>'schemeVersionId')::uuid),
    'overrides',sel.data->'overrides','selectedBy',sel.created_by,'selectedAt',sel.created_at) END,
  'previousSelection',CASE WHEN prev.id IS NULL THEN NULL ELSE jsonb_build_object('fromRevisionId',prev.revision_id,
    'presetVersionId',prev.data->'presetVersionId','schemeVersionId',prev.data->'schemeVersionId','overrides',prev.data->'overrides') END));
END $$;

DO $grants$ DECLARE t text;r text;s record;BEGIN
 ALTER TABLE revision_style_selections ENABLE ROW LEVEL SECURITY;
 REVOKE ALL ON revision_style_selections FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated','fire3d_api','fet3d_backend_executor'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON revision_style_selections FROM %I',r);END IF;END LOOP;
 GRANT SELECT,INSERT ON revision_style_selections TO fet3d_ifc_upload_owner;
 CREATE POLICY style_gate_owner ON revision_style_selections TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
 -- Pending-registration cleanup deletes users; the RESTRICT foreign key check must see these rows.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON revision_style_selections TO fet3d_pending_cleanup_owner;
  CREATE POLICY pending_cleanup_owner ON revision_style_selections FOR SELECT TO fet3d_pending_cleanup_owner USING(true);
 END IF;
 FOREACH t IN ARRAY ARRAY['revisions','buildings','organizations','organization_library_items','organization_library_versions','users','audit_logs'] LOOP
  EXECUTE format('GRANT SELECT ON %I TO fet3d_ifc_upload_owner',t);
  IF (SELECT relrowsecurity FROM pg_class WHERE oid=format('public.%I',t)::regclass) AND NOT EXISTS(SELECT 1 FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=t
     AND ('fet3d_ifc_upload_owner'=ANY(p.roles) OR 'public'=ANY(p.roles)) AND p.cmd IN('ALL','SELECT')) THEN
   EXECUTE format('CREATE POLICY style_gate_owner_read ON %I FOR SELECT TO fet3d_ifc_upload_owner USING(true)',t);
  END IF;
 END LOOP;
 GRANT INSERT ON audit_logs TO fet3d_ifc_upload_owner;
 ALTER FUNCTION revision_style_library_ref(uuid,text) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION revision_style_gate(text,uuid,uuid,uuid,jsonb,bigint) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION library_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION revision_style_library_ref(uuid,text) FROM PUBLIC;
 REVOKE ALL ON FUNCTION revision_style_gate(text,uuid,uuid,uuid,jsonb,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('REVOKE ALL ON FUNCTION revision_style_gate(text,uuid,uuid,uuid,jsonb,bigint) FROM %I',r);
  EXECUTE format('REVOKE ALL ON FUNCTION revision_style_library_ref(uuid,text) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION revision_style_gate(text,uuid,uuid,uuid,jsonb,bigint) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM revision_style_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
