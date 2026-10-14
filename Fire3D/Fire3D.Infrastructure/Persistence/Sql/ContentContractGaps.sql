-- Contract gaps from issue #86 plus Learn source/situation rules found in the 14/10 Docs review:
-- * admin Learn list rows carry title/kind of the latest version and the open Draft (never-published posts had only a slug);
-- * Show rechecks the publish rules (approved Common sources, active situations); RAG eligibility drops retired sources;
-- * situations can be renamed, described or deactivated (UpdateSituation), never deleted;
-- * a Trainee can read the bookmark state of one post;
-- * review summaries name the submitter/decider and admin training analytics name trainings and organizations.
-- Every function keeps its signature, so owners and EXECUTE grants are unchanged by OR REPLACE.
CREATE TEMP TABLE content_gap_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO content_gap_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Admin list row: post state plus the latest version's title/kind (also for never-published posts) and the open Draft.
CREATE OR REPLACE FUNCTION learn_post_admin_summary(p_post uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT learn_post_representation(p.id,false)||jsonb_build_object(
  'title',lv.title,'kind',lv.content_kind,'latestVersionNumber',lv.version_number,'latestVersionStatus',lv.status,
  'draftVersionId',dv.id,'draftVersionNumber',dv.version_number,'versionCount',(SELECT count(*) FROM learn_post_versions WHERE post_id=p.id))
 FROM learn_posts p
 LEFT JOIN LATERAL (SELECT * FROM learn_post_versions WHERE post_id=p.id ORDER BY version_number DESC LIMIT 1) lv ON true
 LEFT JOIN LATERAL (SELECT * FROM learn_post_versions WHERE post_id=p.id AND status='Draft' ORDER BY version_number DESC LIMIT 1) dv ON true
 WHERE p.id=p_post
$$;

CREATE OR REPLACE FUNCTION learn_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;p learn_posts;v learn_post_versions;src record;receipt content_command_receipts;h text;result jsonb;new_post uuid;new_version uuid;n integer;stamp timestamptz:=clock_timestamp();
 content jsonb;sid text;
BEGIN
 IF p_action NOT IN('CreateSituation','UpdateSituation','CreatePost','CreateVersion','UpdateVersion','PublishVersion','Hide','Show','Delete','Restore','List','Get','GetVersion') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='List' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object(
   'items',COALESCE((SELECT jsonb_agg(learn_post_admin_summary(x.id) ORDER BY x.updated_at DESC,x.id) FROM (SELECT id,updated_at FROM learn_posts WHERE p_input->>'status' IS NULL OR publication_status=p_input->>'status' ORDER BY updated_at DESC,id OFFSET (COALESCE((p_input->>'page')::int,1)-1)::bigint*COALESCE((p_input->>'pageSize')::int,20) LIMIT COALESCE((p_input->>'pageSize')::int,20)) x),'[]'),
   'total',(SELECT count(*) FROM learn_posts WHERE p_input->>'status' IS NULL OR publication_status=p_input->>'status'),'page',COALESCE((p_input->>'page')::int,1),'pageSize',COALESCE((p_input->>'pageSize')::int,20)));
 ELSIF p_action='Get' THEN
  IF NOT EXISTS(SELECT 1 FROM learn_posts WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',learn_post_representation(p_resource,true));
 ELSIF p_action='GetVersion' THEN
  IF NOT EXISTS(SELECT 1 FROM learn_post_versions WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',learn_version_representation(p_resource));
 END IF;

 IF p_action='UpdateSituation' THEN
  -- Rename, describe or deactivate a situation. An inactive situation no longer matches public filters and cannot be
  -- attached to a newly published version; published versions keep their links. Never deleted, so links stay valid.
  IF (p_input ? 'name' AND (NULLIF(btrim(p_input->>'name'),'') IS NULL OR length(p_input->>'name')>255))
   OR (p_input ? 'description' AND length(COALESCE(p_input->>'description',''))>2000)
   OR (p_input ? 'isActive' AND jsonb_typeof(p_input->'isActive')<>'boolean')
   OR NOT (p_input ?| ARRAY['name','description','isActive']) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:learn-situation-id:'||p_resource,0));
  UPDATE learn_situations SET name=CASE WHEN p_input ? 'name' THEN btrim(p_input->>'name') ELSE name END,
   description=CASE WHEN p_input ? 'description' THEN NULLIF(btrim(p_input->>'description'),'') ELSE description END,
   is_active=CASE WHEN p_input ? 'isActive' THEN (p_input->>'isActive')::boolean ELSE is_active END,updated_at=stamp
   WHERE id=p_resource RETURNING jsonb_build_object('id',id,'slug',slug,'name',name,'description',description,'isActive',is_active,'updatedAt',updated_at) INTO result;
  IF result IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  PERFORM content_invalidate('learnSituation',p_resource,(extract(epoch FROM stamp)*1000000)::bigint,jsonb_build_object('operation',p_action,'isActive',result->'isActive'));
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,NULL,'User','Update','LearnSituation',p_resource,gen_random_uuid(),jsonb_build_object('operation',p_action)||(p_input-'slug'),now());
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;
 IF p_action IN('CreateSituation','CreatePost','CreateVersion') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  PERFORM pg_advisory_xact_lock(hashtextextended('content:'||p_actor||':'||p_action||':'||p_key,0));
  SELECT * INTO receipt FROM content_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result);
  END IF;
 ELSIF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;

 IF p_action='CreateSituation' THEN
  IF p_input->>'slug' !~ '^[a-z0-9]+(-[a-z0-9]+)*$' OR length(p_input->>'slug')>120 OR NULLIF(btrim(p_input->>'name'),'') IS NULL OR length(p_input->>'name')>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:learn-situation:'||(p_input->>'slug'),0));
  IF EXISTS(SELECT 1 FROM learn_situations WHERE slug=p_input->>'slug') THEN RETURN jsonb_build_object('code','SLUG_EXISTS','status',409);END IF;
  INSERT INTO learn_situations(id,slug,name,description,created_by,created_at,updated_at) VALUES(gen_random_uuid(),p_input->>'slug',btrim(p_input->>'name'),NULLIF(btrim(p_input->>'description'),''),p_actor,stamp,stamp) RETURNING jsonb_build_object('id',id,'slug',slug,'name',name,'description',description) INTO result;
  INSERT INTO content_command_receipts VALUES(p_actor,p_action,p_key,h,result,stamp);
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;

 IF p_action='CreatePost' THEN
  IF p_input->>'slug' !~ '^[a-z0-9]+(-[a-z0-9]+)*$' OR length(p_input->>'slug')>180 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:learn-post:'||(p_input->>'slug'),0));
  IF EXISTS(SELECT 1 FROM learn_posts WHERE slug=p_input->>'slug') THEN RETURN jsonb_build_object('code','SLUG_EXISTS','status',409);END IF;
  new_post:=gen_random_uuid();
  INSERT INTO learn_posts(id,slug,publication_status,created_by,created_at,updated_at) VALUES(new_post,p_input->>'slug','Unpublished',p_actor,stamp,stamp) RETURNING * INTO p;
 ELSIF p_action='CreateVersion' THEN
  SELECT * INTO p FROM learn_posts WHERE id=p_resource FOR UPDATE;
  IF p.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p.publication_status='Deleted' THEN RETURN jsonb_build_object('code','LEARN_POST_DELETED','status',409);END IF;
 ELSIF p_action IN('UpdateVersion','PublishVersion') THEN
  SELECT * INTO v FROM learn_post_versions WHERE id=p_resource;
  SELECT * INTO p FROM learn_posts WHERE id=v.post_id FOR UPDATE;
  SELECT * INTO v FROM learn_post_versions WHERE id=p_resource FOR UPDATE;
  IF v.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF v.revision_no<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF v.status<>'Draft' THEN RETURN jsonb_build_object('code','LEARN_VERSION_PUBLISHED','status',409);END IF;
  IF p.publication_status='Deleted' THEN RETURN jsonb_build_object('code','LEARN_POST_DELETED','status',409);END IF;
 ELSE
  SELECT * INTO p FROM learn_posts WHERE id=p_resource FOR UPDATE;
  IF p.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p.revision_no<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
 END IF;

 IF p_action IN('CreatePost','CreateVersion','UpdateVersion') THEN
  content:=CASE WHEN p_action='CreatePost' THEN p_input->'version' ELSE p_input END;
  -- Content shape and media are validated and canonicalised by the backend validator before this gate.
  IF jsonb_typeof(content->'blocks') IS DISTINCT FROM 'array' OR content->>'kind' NOT IN('Article','Tip','Video') OR NULLIF(btrim(content->>'title'),'') IS NULL OR NULLIF(btrim(content->>'summary'),'') IS NULL THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(COALESCE(content->'situationIds','[]')) x WHERE NOT EXISTS(SELECT 1 FROM learn_situations WHERE id=x::uuid AND is_active)) THEN
   RETURN jsonb_build_object('code','LEARN_SITUATION_INVALID','status',422);END IF;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(content->'sources','[]')) x WHERE NOT EXISTS(SELECT 1 FROM knowledge_sources WHERE id=(x->>'sourceId')::uuid AND visibility='Common')) THEN
   RETURN jsonb_build_object('code','LEARN_SOURCE_INVALID','status',422);END IF;
  IF p_action='UpdateVersion' THEN
   UPDATE learn_post_versions SET content_schema_version=content->>'contentSchemaVersion',content_kind=content->>'kind',title=btrim(content->>'title'),summary=btrim(content->>'summary'),
    cover_image_url=content->>'coverImageUrl',content_blocks=content->'blocks',content_hash=fet3d_jsonb_payload_hash(content),revision_no=revision_no+1,updated_at=stamp WHERE id=v.id RETURNING * INTO v;
   DELETE FROM learn_post_version_situations WHERE post_version_id=v.id;DELETE FROM learn_post_version_sources WHERE post_version_id=v.id;
  ELSE
   SELECT COALESCE(max(version_number),0)+1 INTO n FROM learn_post_versions WHERE post_id=p.id;
   INSERT INTO learn_post_versions(id,post_id,version_number,content_schema_version,content_kind,title,summary,cover_image_url,content_blocks,content_hash,status,created_by,created_at,updated_at)
   VALUES(gen_random_uuid(),p.id,n,content->>'contentSchemaVersion',content->>'kind',btrim(content->>'title'),btrim(content->>'summary'),content->>'coverImageUrl',content->'blocks',fet3d_jsonb_payload_hash(content),'Draft',p_actor,stamp,stamp) RETURNING * INTO v;
  END IF;
  INSERT INTO learn_post_version_situations(post_version_id,situation_id) SELECT v.id,x::uuid FROM jsonb_array_elements_text(COALESCE(content->'situationIds','[]')) x ON CONFLICT DO NOTHING;
  INSERT INTO learn_post_version_sources(post_version_id,source_id,source_role,locator) SELECT v.id,(x->>'sourceId')::uuid,COALESCE(x->>'role','Reference'),COALESCE(x->'locator','{}') FROM jsonb_array_elements(COALESCE(content->'sources','[]')) x ON CONFLICT DO NOTHING;
 END IF;

 -- Publish: version becomes immutable and the post points to it; Hidden stays Hidden, a new post becomes Published.
 IF p_action='PublishVersion' OR (p_action='CreatePost' AND (p_input->>'publish')::boolean IS TRUE) THEN
  FOR src IN SELECT k.* FROM learn_post_version_sources l JOIN knowledge_sources k ON k.id=l.source_id WHERE l.post_version_id=v.id LOOP
   IF src.approval_status<>'Approved' OR src.visibility<>'Common' THEN RETURN jsonb_build_object('code','LEARN_SOURCE_NOT_APPROVED','status',409);END IF;
  END LOOP;
  IF EXISTS(SELECT 1 FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id WHERE l.post_version_id=v.id AND NOT s.is_active) THEN RETURN jsonb_build_object('code','LEARN_SITUATION_INVALID','status',409);END IF;
  UPDATE learn_post_versions SET status='Published',published_by=p_actor,published_at=stamp,revision_no=revision_no+1,updated_at=stamp WHERE id=v.id RETURNING * INTO v;
  UPDATE learn_posts SET published_version_id=v.id,publication_status=CASE WHEN publication_status='Hidden' THEN 'Hidden' ELSE 'Published' END,revision_no=revision_no+1,updated_at=stamp WHERE id=p.id RETURNING * INTO p;
 ELSIF p_action='Hide' THEN
  IF p.publication_status<>'Published' THEN RETURN jsonb_build_object('code','LEARN_STATE_CONFLICT','status',409);END IF;
  UPDATE learn_posts SET publication_status='Hidden',revision_no=revision_no+1,updated_at=stamp WHERE id=p.id RETURNING * INTO p;
 ELSIF p_action='Show' THEN
  IF p.publication_status<>'Hidden' THEN RETURN jsonb_build_object('code','LEARN_STATE_CONFLICT','status',409);END IF;
  -- Showing makes the published version public again, so it must still meet the publish rules: every cited source
  -- approved Common (a source retired while hidden blocks it) and every situation active.
  IF EXISTS(SELECT 1 FROM learn_post_version_sources l JOIN knowledge_sources k ON k.id=l.source_id
   WHERE l.post_version_id=p.published_version_id AND (k.approval_status<>'Approved' OR k.visibility<>'Common')) THEN
   RETURN jsonb_build_object('code','LEARN_SOURCE_NOT_APPROVED','status',409);END IF;
  IF EXISTS(SELECT 1 FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id WHERE l.post_version_id=p.published_version_id AND NOT s.is_active)
   THEN RETURN jsonb_build_object('code','LEARN_SITUATION_INVALID','status',409);END IF;
  UPDATE learn_posts SET publication_status='Published',revision_no=revision_no+1,updated_at=stamp WHERE id=p.id RETURNING * INTO p;
 ELSIF p_action='Delete' THEN
  IF p.publication_status='Deleted' THEN RETURN jsonb_build_object('code','LEARN_STATE_CONFLICT','status',409);END IF;
  UPDATE learn_posts SET publication_status='Deleted',revision_no=revision_no+1,updated_at=stamp WHERE id=p.id RETURNING * INTO p;
 ELSIF p_action='Restore' THEN
  -- A previously public post returns Hidden, never directly public.
  IF p.publication_status<>'Deleted' THEN RETURN jsonb_build_object('code','LEARN_STATE_CONFLICT','status',409);END IF;
  UPDATE learn_posts SET publication_status=CASE WHEN published_version_id IS NULL THEN 'Unpublished' ELSE 'Hidden' END,revision_no=revision_no+1,updated_at=stamp WHERE id=p.id RETURNING * INTO p;
 ELSIF p_action IN('CreateVersion','UpdateVersion') THEN
  UPDATE learn_posts SET updated_at=stamp WHERE id=p.id;
 END IF;

 IF p_action IN('PublishVersion','Hide','Show','Delete','Restore') OR (p_action='CreatePost' AND (p_input->>'publish')::boolean IS TRUE) THEN
  PERFORM content_invalidate('learn',p.id,p.revision_no,jsonb_build_object('slug',p.slug,'operation',p_action,'publicationStatus',p.publication_status));
 END IF;
 result:=CASE WHEN p_action IN('CreateVersion','UpdateVersion') THEN learn_version_representation(v.id) ELSE learn_post_representation(p.id,true) END;
 IF p_key IS NOT NULL AND p_action IN('CreatePost','CreateVersion') THEN INSERT INTO content_command_receipts VALUES(p_actor,p_action,p_key,h,result,stamp);END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User','Update',CASE WHEN p_action IN('CreateVersion','UpdateVersion','PublishVersion') THEN 'LearnPostVersion' ELSE 'LearnPost' END,
  CASE WHEN p_action IN('CreateVersion','UpdateVersion','PublishVersion') THEN v.id ELSE p.id END,gen_random_uuid(),
  jsonb_build_object('operation',p_action,'postId',p.id,'publicationStatus',p.publication_status,'contentHash',v.content_hash),now());
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

CREATE OR REPLACE FUNCTION learn_rag_eligible(p_post uuid,p_version uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT EXISTS(SELECT 1 FROM learn_posts p JOIN learn_post_versions v ON v.id=p.published_version_id AND v.post_id=p.id
  WHERE p.id=p_post AND v.id=p_version AND v.status='Published' AND p.publication_status IN('Published','Hidden')
   AND NOT EXISTS(SELECT 1 FROM learn_post_version_sources l JOIN knowledge_sources k ON k.id=l.source_id
    WHERE l.post_version_id=v.id AND (k.approval_status<>'Approved' OR k.visibility<>'Common')))
$$;

CREATE OR REPLACE FUNCTION learn_public_gate(p_action text,p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;p learn_posts;result jsonb;total integer;search text;pattern text;situations text[];kinds text[];page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);
BEGIN
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 IF p_action='Situations' THEN
  RETURN jsonb_build_object('code','OK','result',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',id,'slug',slug,'name',name,'description',description) ORDER BY name,id) FROM learn_situations WHERE is_active),'[]'));
 ELSIF p_action='Posts' THEN
  -- Search folds case and Vietnamese diacritics on title+summary; situations match any listed active slug (OR); kinds
  -- match any listed kind (OR); the three filters combine with AND. The legacy single 'situation' joins the list.
  search:=NULLIF(btrim(p_input->>'search'),'');
  situations:=ARRAY(SELECT DISTINCT x FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(p_input->'situations')='array' THEN p_input->'situations' ELSE '[]' END) x
   UNION SELECT p_input->>'situation' WHERE NULLIF(p_input->>'situation','') IS NOT NULL);
  kinds:=ARRAY(SELECT DISTINCT x FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(p_input->'kinds')='array' THEN p_input->'kinds' ELSE '[]' END) x);
  IF (search IS NOT NULL AND char_length(search) NOT BETWEEN 2 AND 100) OR cardinality(situations)>10
   OR EXISTS(SELECT 1 FROM unnest(kinds) k WHERE k NOT IN('Article','Tip','Video')) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);
  END IF;
  pattern:=catalog_search_pattern(learn_fold_text(search));
  WITH f AS MATERIALIZED (
   SELECT lp.id,lp.slug,v.content_kind,v.title,v.summary,v.cover_image_url,v.published_at
   FROM learn_posts lp JOIN learn_post_versions v ON v.id=lp.published_version_id AND v.post_id=lp.id
   WHERE lp.publication_status='Published' AND v.status='Published'
    AND (cardinality(kinds)=0 OR v.content_kind=ANY(kinds))
    AND (cardinality(situations)=0 OR EXISTS(SELECT 1 FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id
     WHERE l.post_version_id=v.id AND s.is_active AND s.slug=ANY(situations)))
    AND (pattern IS NULL OR learn_fold_text(v.title||' '||v.summary) LIKE pattern ESCAPE '\'))
  SELECT (SELECT count(*) FROM f),(SELECT COALESCE(jsonb_agg(jsonb_build_object('id',x.id,'slug',x.slug,'kind',x.content_kind,'title',x.title,'summary',x.summary,
    'coverImageUrl',x.cover_image_url,'publishedAt',x.published_at) ORDER BY x.published_at DESC,x.id),'[]')
   FROM (SELECT * FROM f ORDER BY published_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x) INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='Post' THEN
  SELECT * INTO p FROM learn_posts WHERE slug=p_input->>'slug';
  IF p.id IS NULL OR p.publication_status IN('Unpublished','Deleted') THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p.publication_status='Hidden' THEN RETURN jsonb_build_object('code','LEARN_POST_UNAVAILABLE','status',410);END IF;
  RETURN jsonb_build_object('code','OK','result',learn_post_representation(p.id,false));
 END IF;
 -- Bookmarks: Trainee with a live session.
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='BookmarkState' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('postId',p_input->>'postId',
   'bookmarked',EXISTS(SELECT 1 FROM learn_bookmarks WHERE trainee_user_id=p_actor AND post_id=(p_input->>'postId')::uuid)));
 END IF;
 IF p_action='Bookmarks' THEN
  SELECT count(*) INTO total FROM learn_bookmarks WHERE trainee_user_id=p_actor;
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.created_at DESC,x.post_id),'[]') INTO result FROM (
   SELECT b.post_id,b.created_at,CASE WHEN lp.publication_status='Published' THEN jsonb_build_object('postId',lp.id,'available',true,'bookmarkedAt',b.created_at,'slug',lp.slug,'title',v.title,'summary',v.summary,'kind',v.content_kind)
    ELSE jsonb_build_object('postId',b.post_id,'available',false,'bookmarkedAt',b.created_at) END item
   FROM learn_bookmarks b JOIN learn_posts lp ON lp.id=b.post_id LEFT JOIN learn_post_versions v ON v.id=lp.published_version_id
   WHERE b.trainee_user_id=p_actor ORDER BY b.created_at DESC,b.post_id OFFSET (page-1)::bigint*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 END IF;
 SELECT * INTO p FROM learn_posts WHERE id=(p_input->>'postId')::uuid;
 IF p_action='Bookmark' THEN
  IF p.id IS NULL OR p.publication_status<>'Published' THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  INSERT INTO learn_bookmarks(trainee_user_id,post_id) VALUES(p_actor,p.id) ON CONFLICT DO NOTHING;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('postId',p.id,'bookmarked',true));
 ELSIF p_action='Unbookmark' THEN
  DELETE FROM learn_bookmarks WHERE trainee_user_id=p_actor AND post_id=(p_input->>'postId')::uuid;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('postId',p_input->>'postId','bookmarked',false));
 END IF;
 RETURN jsonb_build_object('code','ACTION_INVALID','status',400);
END $$;

CREATE OR REPLACE FUNCTION scenario_review_summary(p_review uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('reviewId',r.id,'scenarioVersionId',v.id,'status',r.status,
  'contentHash',r.content_hash,'rubricHash',r.rubric_hash,'submittedBy',r.submitted_by,'submittedByName',COALESCE(NULLIF(btrim(su.full_name),''),su.username,su.email),'submittedAt',r.submitted_at,
  'decidedBy',r.reviewed_by,'decidedByName',COALESCE(NULLIF(btrim(du.full_name),''),du.username,du.email),'decidedAt',r.reviewed_at,'reason',r.reason,'organizationId',o.id,'organizationName',o.name,
  'buildingId',b.id,'buildingName',b.name,'scenarioId',s.id,'scenarioName',s.name,'versionNumber',v.version_number)
 FROM scenario_content_reviews r JOIN scenario_versions v ON v.id=r.scenario_version_id
 JOIN scenarios s ON s.id=v.scenario_id JOIN buildings b ON b.id=v.building_id JOIN organizations o ON o.id=b.organization_id
 LEFT JOIN users su ON su.id=r.submitted_by LEFT JOIN users du ON du.id=r.reviewed_by
 WHERE r.id=p_review;
$$;

CREATE OR REPLACE FUNCTION analytics_training(p_org uuid,p_from timestamptz,p_to timestamptz,p_as_of timestamptz,p_active_seconds integer) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH cohort AS (
  SELECT s.id,s.organization_id,s.training_id,s.trainee_user_id,s.mode::text mode,s.launched_at,s.ended_at,r.outcome,r.created_at result_at
  FROM sessions s LEFT JOIN LATERAL (SELECT x.outcome,x.created_at FROM session_results x WHERE x.session_id=s.id AND x.contract_version=7 AND x.created_at<=p_as_of ORDER BY x.created_at,x.id LIMIT 1) r ON true
  WHERE s.contract_version=7 AND s.started_at>=p_from AND s.started_at<p_to AND s.started_at<=p_as_of AND (p_org IS NULL OR s.organization_id=p_org)),
 totals AS (SELECT count(*) plays,count(DISTINCT trainee_user_id) trainees,count(result_at) completed,
   count(*) FILTER(WHERE outcome IN('Passed','NotPassed')) assessed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort),
 durations AS (SELECT extract(epoch FROM ended_at-launched_at) seconds FROM cohort WHERE result_at IS NOT NULL AND launched_at IS NOT NULL AND ended_at IS NOT NULL AND ended_at<=p_as_of AND ended_at>=launched_at)
 SELECT jsonb_build_object(
  'plays',t.plays,'uniqueTrainees',t.trainees,'completed',t.completed,
  'completionRate',CASE WHEN t.plays>0 THEN round(t.completed::numeric/t.plays,4) END,
  'activeSessions',(SELECT count(*) FROM sessions s WHERE s.contract_version=7 AND s.status='Running' AND s.last_heartbeat_at>p_as_of-make_interval(secs=>p_active_seconds)
    AND s.last_heartbeat_at<=p_as_of AND (p_org IS NULL OR s.organization_id=p_org)),
  'outcomes',(SELECT jsonb_object_agg(o.outcome,(SELECT count(*) FROM cohort c WHERE c.outcome=o.outcome)) FROM unnest(ARRAY['Passed','NotPassed','Incomplete','NotAssessed']) o(outcome)),
  'assessment',jsonb_build_object('assessed',t.assessed,'passed',t.passed,'passRate',CASE WHEN t.assessed>0 THEN round(t.passed::numeric/t.assessed,4) END),
  'duration',(SELECT jsonb_build_object('sessions',count(*),'averageSeconds',round(avg(seconds)::numeric,1),'medianSeconds',round((percentile_cont(0.5) WITHIN GROUP(ORDER BY seconds))::numeric,1)) FROM durations),
  'byMode',(SELECT jsonb_agg(jsonb_build_object('mode',m.mode,'plays',(SELECT count(*) FROM cohort c WHERE c.mode=m.mode),'completed',(SELECT count(result_at) FROM cohort c WHERE c.mode=m.mode)) ORDER BY m.ord)
    FROM unnest(ARRAY['Learn','Guided','Assessment']) WITH ORDINALITY m(mode,ord)),
  'byTraining',COALESCE((SELECT jsonb_agg(jsonb_build_object('trainingId',x.training_id,'trainingName',(SELECT name FROM trainings WHERE id=x.training_id),'plays',x.plays,'completed',x.completed,'passed',x.passed) ORDER BY x.plays DESC,x.training_id) FROM (
    SELECT training_id,count(*) plays,count(result_at) completed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort GROUP BY training_id ORDER BY count(*) DESC,training_id LIMIT 100) x),'[]'),
  'byOrganization',CASE WHEN p_org IS NULL THEN COALESCE((SELECT jsonb_agg(jsonb_build_object('organizationId',x.organization_id,'organizationName',(SELECT name FROM organizations WHERE id=x.organization_id),'plays',x.plays,'uniqueTrainees',x.trainees,'completed',x.completed) ORDER BY x.plays DESC,x.organization_id) FROM (
    SELECT organization_id,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(result_at) completed FROM cohort GROUP BY organization_id ORDER BY count(*) DESC,organization_id LIMIT 100) x),'[]') END)
 FROM totals t
$$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION learn_post_admin_summary(uuid) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION learn_post_admin_summary(uuid) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION learn_post_admin_summary(uuid) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM content_gap_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
