-- Learn bookmark filters (issue #151): the Trainee bookmark list takes the kind (repeated, OR) and search filters of the public
-- post list and returns per-kind counts of every bookmark. The gate keeps its signature, so owner and EXECUTE grants are
-- unchanged by OR REPLACE.
CREATE TEMP TABLE learn_bookmark_filter_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learn_bookmark_filter_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION learn_public_gate(p_action text,p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;p learn_posts;result jsonb;counts jsonb;total integer;search text;pattern text;situations text[];kinds text[];page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);
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
  -- kind (OR) and search (as for Posts) combine with AND and only match bookmarks of Published posts, so bookmarks that
  -- are no longer public are listed only without filters. counts cover every bookmark of the Trainee, whatever the
  -- filters and page; their sum is the unfiltered total.
  search:=NULLIF(btrim(p_input->>'search'),'');
  kinds:=ARRAY(SELECT DISTINCT x FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(p_input->'kinds')='array' THEN p_input->'kinds' ELSE '[]' END) x);
  IF (search IS NOT NULL AND char_length(search) NOT BETWEEN 2 AND 100) OR EXISTS(SELECT 1 FROM unnest(kinds) k WHERE k NOT IN('Article','Tip','Video')) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);
  END IF;
  pattern:=catalog_search_pattern(learn_fold_text(search));
  WITH bm AS MATERIALIZED (
   SELECT b.post_id,b.created_at,lp.publication_status='Published' AS available,lp.slug,v.title,v.summary,v.content_kind
   FROM learn_bookmarks b JOIN learn_posts lp ON lp.id=b.post_id LEFT JOIN learn_post_versions v ON v.id=lp.published_version_id
   WHERE b.trainee_user_id=p_actor),
  f AS MATERIALIZED (
   SELECT * FROM bm WHERE (cardinality(kinds)=0 AND pattern IS NULL)
    OR (bm.available AND (cardinality(kinds)=0 OR bm.content_kind=ANY(kinds)) AND (pattern IS NULL OR learn_fold_text(bm.title||' '||bm.summary) LIKE pattern ESCAPE '\')))
  SELECT (SELECT count(*) FROM f),
   (SELECT COALESCE(jsonb_agg(CASE WHEN x.available THEN jsonb_build_object('postId',x.post_id,'available',true,'bookmarkedAt',x.created_at,'slug',x.slug,'title',x.title,'summary',x.summary,'kind',x.content_kind)
     ELSE jsonb_build_object('postId',x.post_id,'available',false,'bookmarkedAt',x.created_at) END ORDER BY x.created_at DESC,x.post_id),'[]')
    FROM (SELECT * FROM f ORDER BY created_at DESC,post_id OFFSET (page-1)::bigint*size LIMIT size) x),
   (SELECT jsonb_build_object('Article',count(*) FILTER(WHERE available AND content_kind='Article'),'Tip',count(*) FILTER(WHERE available AND content_kind='Tip'),
     'Video',count(*) FILTER(WHERE available AND content_kind='Video'),'unavailable',count(*) FILTER(WHERE NOT available)) FROM bm)
   INTO total,result,counts;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size,'counts',counts));
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

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM learn_bookmark_filter_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
