-- Editor catalogs for PlatformAdmin: knowledge sources (visibility/status/search, paged) and Learn situations (search/active,
-- paged). Read-only gates; nothing is seeded. The editor asks for visibility=Common so tenant sources never enter its catalog;
-- the admin registry lists every visibility by default so tenant sources can still be reviewed and approved.
CREATE TEMP TABLE catalog_lists_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO catalog_lists_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION catalog_search_pattern(p_text text) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN NULLIF(btrim(p_text),'') IS NULL THEN NULL ELSE '%'||replace(replace(replace(btrim(p_text),'\','\\'),'%','\%'),'_','\_')||'%' END
$$;

CREATE FUNCTION catalog_list_gate(p_action text,p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;page integer;size integer;pattern text;result jsonb;total integer;
BEGIN
 IF p_action NOT IN('KnowledgeSources','Situations') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 pattern:=catalog_search_pattern(p_input->>'search');
 IF p_action='KnowledgeSources' THEN
  WITH f AS MATERIALIZED(SELECT k.* FROM knowledge_sources k WHERE (p_input->>'visibility' IS NULL OR k.visibility=p_input->>'visibility')
    AND (p_input->>'id' IS NULL OR k.id=(p_input->>'id')::uuid)
    AND (p_input->>'status' IS NULL OR k.approval_status=p_input->>'status')
    AND (pattern IS NULL OR k.title ILIKE pattern ESCAPE '\' OR k.version_label ILIKE pattern ESCAPE '\' OR COALESCE(k.jurisdiction,'') ILIKE pattern ESCAPE '\'))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(x.item ORDER BY x.created_at DESC,x.id) FROM (
    SELECT f.id,f.created_at,jsonb_build_object('id',f.id,'visibility',f.visibility,'organizationId',f.organization_id,'title',f.title,'sourceUri',f.source_uri,
     'versionLabel',f.version_label,'sourceHash',f.source_hash,'jurisdiction',f.jurisdiction,'effectiveFrom',f.effective_from,'effectiveUntil',f.effective_until,
     'approvalStatus',f.approval_status,'approvedAt',f.approved_at,'createdAt',f.created_at,'revision',f.revision,
     'canUseInDraft',f.visibility='Common' AND f.approval_status IN('Draft','Approved'),
     'canPublishReference',f.visibility='Common' AND f.approval_status='Approved' AND (f.effective_from IS NULL OR f.effective_from<=clock_timestamp())
      AND (f.effective_until IS NULL OR f.effective_until>clock_timestamp())) item
    FROM f ORDER BY f.created_at DESC,f.id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 ELSE
  WITH f AS MATERIALIZED(SELECT s.* FROM learn_situations s WHERE (p_input->>'isActive' IS NULL OR s.is_active=(p_input->>'isActive')::boolean)
    AND (pattern IS NULL OR s.slug ILIKE pattern ESCAPE '\' OR s.name ILIKE pattern ESCAPE '\'))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(x.item ORDER BY x.name,x.id) FROM (
    SELECT f.id,f.name,jsonb_build_object('id',f.id,'slug',f.slug,'name',f.name,'description',f.description,'isActive',f.is_active,'createdAt',f.created_at,'updatedAt',f.updated_at) item
    FROM f ORDER BY f.name,f.id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
END $$;

DO $grants$ DECLARE sig text;r text;s record;BEGIN
 FOREACH sig IN ARRAY ARRAY['catalog_search_pattern(text)','catalog_list_gate(text,uuid,uuid,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION catalog_list_gate(text,uuid,uuid,jsonb) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM catalog_lists_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
