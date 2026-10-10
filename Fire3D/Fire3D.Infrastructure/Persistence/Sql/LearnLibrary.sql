-- Learn CMS, Common knowledge source registry and Organization Library. Additive; nothing is seeded.
CREATE TEMP TABLE learn_library_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learn_library_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Knowledge sources: Admin-governed Common sources (and tenant sources) with an explicit approval state.
CREATE TABLE knowledge_sources(
 id uuid PRIMARY KEY,organization_id uuid REFERENCES organizations(id),visibility text NOT NULL CHECK(visibility IN('Common','Organization')),
 title text NOT NULL CHECK(NULLIF(btrim(title),'') IS NOT NULL),source_uri text,version_label varchar(100) NOT NULL,
 source_hash varchar(64) NOT NULL CHECK(source_hash ~ '^[0-9a-f]{64}$'),jurisdiction text,effective_from timestamptz,effective_until timestamptz,
 approval_status text NOT NULL CHECK(approval_status IN('Draft','Approved','Retired')),approved_by uuid REFERENCES users(id),approved_at timestamptz,
 created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,revision bigint NOT NULL DEFAULT 1 CHECK(revision>0),
 CHECK(visibility='Common' AND organization_id IS NULL OR visibility='Organization' AND organization_id IS NOT NULL),
 CHECK((approval_status='Draft')=(approved_at IS NULL)),CHECK(effective_until IS NULL OR effective_from IS NULL OR effective_until>effective_from));
CREATE UNIQUE INDEX knowledge_sources_identity ON knowledge_sources(COALESCE(organization_id,'00000000-0000-0000-0000-000000000000'::uuid),visibility,source_hash,version_label);

CREATE TABLE learn_situations(
 id uuid PRIMARY KEY,slug varchar(120) NOT NULL UNIQUE CHECK(slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),name varchar(255) NOT NULL CHECK(NULLIF(btrim(name),'') IS NOT NULL),
 description text,is_active boolean NOT NULL DEFAULT true,created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL);
CREATE TABLE learn_posts(
 id uuid PRIMARY KEY,slug varchar(180) NOT NULL UNIQUE CHECK(slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
 publication_status text NOT NULL CHECK(publication_status IN('Unpublished','Published','Hidden','Deleted')),published_version_id uuid,
 created_by uuid NOT NULL REFERENCES users(id),revision_no bigint NOT NULL DEFAULT 1 CHECK(revision_no>0),created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,
 CHECK(publication_status NOT IN('Published','Hidden') OR published_version_id IS NOT NULL));
CREATE TABLE learn_post_versions(
 id uuid PRIMARY KEY,post_id uuid NOT NULL REFERENCES learn_posts(id),version_number integer NOT NULL CHECK(version_number>0),
 content_schema_version varchar(50) NOT NULL,content_kind text NOT NULL CHECK(content_kind IN('Article','Tip','Video')),
 title varchar(255) NOT NULL CHECK(NULLIF(btrim(title),'') IS NOT NULL),summary text NOT NULL CHECK(NULLIF(btrim(summary),'') IS NOT NULL),cover_image_url text,
 content_blocks jsonb NOT NULL CHECK(jsonb_typeof(content_blocks)='array'),content_hash varchar(64) NOT NULL CHECK(content_hash ~ '^[0-9a-f]{64}$'),
 status text NOT NULL CHECK(status IN('Draft','Published')),created_by uuid NOT NULL REFERENCES users(id),published_by uuid REFERENCES users(id),published_at timestamptz,
 revision_no bigint NOT NULL DEFAULT 1 CHECK(revision_no>0),created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,
 UNIQUE(post_id,version_number),UNIQUE(id,post_id),
 CHECK((status='Draft' AND published_by IS NULL AND published_at IS NULL) OR (status='Published' AND published_by IS NOT NULL AND published_at IS NOT NULL)));
ALTER TABLE learn_posts ADD CONSTRAINT learn_post_published_version FOREIGN KEY(published_version_id,id) REFERENCES learn_post_versions(id,post_id) DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE learn_post_version_situations(post_version_id uuid NOT NULL REFERENCES learn_post_versions(id),situation_id uuid NOT NULL REFERENCES learn_situations(id),
 created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(post_version_id,situation_id));
CREATE TABLE learn_post_version_sources(post_version_id uuid NOT NULL REFERENCES learn_post_versions(id),source_id uuid NOT NULL REFERENCES knowledge_sources(id),
 source_role text NOT NULL DEFAULT 'Reference' CHECK(source_role IN('Primary','Reference','Transcript')),locator jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(locator)='object'),
 created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(post_version_id,source_id,source_role));
CREATE TABLE learn_bookmarks(trainee_user_id uuid NOT NULL REFERENCES users(id),post_id uuid NOT NULL REFERENCES learn_posts(id),created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(trainee_user_id,post_id));
CREATE TABLE content_command_receipts(actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),result jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(actor_id,operation,idempotency_key));

-- Published versions and their links are immutable; drafts change only through the gate.
CREATE FUNCTION immutable_published_content() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Content history is retained'; END IF;
 IF OLD.status='Published' THEN RAISE EXCEPTION 'Published versions are immutable; create a new version'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER immutable_learn_version BEFORE UPDATE OR DELETE ON learn_post_versions FOR EACH ROW EXECUTE FUNCTION immutable_published_content();
CREATE FUNCTION immutable_published_links() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM learn_post_versions WHERE id=COALESCE(OLD.post_version_id,NEW.post_version_id) AND status='Published') THEN RAISE EXCEPTION 'Published version links are immutable'; END IF;
 RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER immutable_learn_situation_links BEFORE INSERT OR UPDATE OR DELETE ON learn_post_version_situations FOR EACH ROW EXECUTE FUNCTION immutable_published_links();
CREATE TRIGGER immutable_learn_source_links BEFORE INSERT OR UPDATE OR DELETE ON learn_post_version_sources FOR EACH ROW EXECUTE FUNCTION immutable_published_links();

CREATE TABLE organization_library_items(
 id uuid PRIMARY KEY,kind text NOT NULL CHECK(kind IN('ScenarioTemplate','RubricSample','Equipment')),code varchar(80) NOT NULL UNIQUE CHECK(code ~ '^[A-Z0-9][A-Z0-9_-]{0,79}$'),
 is_active boolean NOT NULL DEFAULT true,created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,revision bigint NOT NULL DEFAULT 1 CHECK(revision>0));
CREATE TABLE organization_library_versions(
 id uuid PRIMARY KEY,item_id uuid NOT NULL REFERENCES organization_library_items(id),version_number integer NOT NULL CHECK(version_number>0),
 name varchar(255) NOT NULL CHECK(NULLIF(btrim(name),'') IS NOT NULL),payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
 required_capabilities jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(required_capabilities)='array'),payload_hash varchar(64) NOT NULL CHECK(payload_hash ~ '^[0-9a-f]{64}$'),
 status text NOT NULL CHECK(status IN('Draft','Published')),published_by uuid REFERENCES users(id),published_at timestamptz,
 created_by uuid NOT NULL REFERENCES users(id),created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,revision bigint NOT NULL DEFAULT 1 CHECK(revision>0),
 UNIQUE(item_id,version_number),CHECK((status='Draft')=(published_at IS NULL)),CHECK((status='Draft')=(published_by IS NULL)));
CREATE TRIGGER immutable_library_version BEFORE UPDATE OR DELETE ON organization_library_versions FOR EACH ROW EXECUTE FUNCTION immutable_published_content();
CREATE FUNCTION retain_library_item() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Retain library history; deactivate the item instead'; END $$;
CREATE TRIGGER retain_library_item BEFORE DELETE ON organization_library_items FOR EACH ROW EXECUTE FUNCTION retain_library_item();

-- Read models.
CREATE FUNCTION learn_version_representation(p_version uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',v.id,'postId',v.post_id,'versionNumber',v.version_number,'contentSchemaVersion',v.content_schema_version,'kind',v.content_kind,
  'title',v.title,'summary',v.summary,'coverImageUrl',v.cover_image_url,'blocks',v.content_blocks,'contentHash',v.content_hash,'status',v.status,
  'publishedAt',v.published_at,'revision',v.revision_no,'createdAt',v.created_at,'updatedAt',v.updated_at,
  'situations',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',s.id,'slug',s.slug,'name',s.name) ORDER BY s.slug) FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id WHERE l.post_version_id=v.id),'[]'),
  'sources',COALESCE((SELECT jsonb_agg(jsonb_build_object('sourceId',k.id,'title',k.title,'versionLabel',k.version_label,'role',l.source_role,'locator',l.locator) ORDER BY k.title,l.source_role) FROM learn_post_version_sources l JOIN knowledge_sources k ON k.id=l.source_id WHERE l.post_version_id=v.id),'[]'))
 FROM learn_post_versions v WHERE v.id=p_version
$$;
CREATE FUNCTION learn_post_representation(p_post uuid,p_admin boolean) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',p.id,'slug',p.slug,'publicationStatus',p.publication_status,'publishedVersionId',p.published_version_id,'revision',p.revision_no,
  'createdAt',p.created_at,'updatedAt',p.updated_at,'publishedVersion',learn_version_representation(p.published_version_id))
  ||CASE WHEN p_admin THEN jsonb_build_object('versions',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',v.id,'versionNumber',v.version_number,'status',v.status,'title',v.title,'revision',v.revision_no,'updatedAt',v.updated_at) ORDER BY v.version_number DESC) FROM learn_post_versions v WHERE v.post_id=p.id),'[]')) ELSE '{}'::jsonb END
 FROM learn_posts p WHERE p.id=p_post
$$;

-- Only a pointer to the current published version of a Published or Hidden post is eligible for retrieval; Deleted never is.
CREATE FUNCTION learn_rag_eligible(p_post uuid,p_version uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT EXISTS(SELECT 1 FROM learn_posts p JOIN learn_post_versions v ON v.id=p.published_version_id AND v.post_id=p.id
  WHERE p.id=p_post AND v.id=p_version AND v.status='Published' AND p.publication_status IN('Published','Hidden'))
$$;

CREATE FUNCTION learn_public_gate(p_action text,p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;p learn_posts;result jsonb;total integer;page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);
BEGIN
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 IF p_action='Situations' THEN
  RETURN jsonb_build_object('code','OK','result',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',id,'slug',slug,'name',name,'description',description) ORDER BY name,id) FROM learn_situations WHERE is_active),'[]'));
 ELSIF p_action='Posts' THEN
  SELECT count(*) INTO total FROM learn_posts lp WHERE lp.publication_status='Published'
   AND (p_input->>'situation' IS NULL OR EXISTS(SELECT 1 FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id WHERE l.post_version_id=lp.published_version_id AND s.slug=p_input->>'situation'));
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.published_at DESC,x.id),'[]') INTO result FROM (
   SELECT lp.id,v.published_at,jsonb_build_object('id',lp.id,'slug',lp.slug,'kind',v.content_kind,'title',v.title,'summary',v.summary,'coverImageUrl',v.cover_image_url,'publishedAt',v.published_at) item
   FROM learn_posts lp JOIN learn_post_versions v ON v.id=lp.published_version_id
   WHERE lp.publication_status='Published' AND (p_input->>'situation' IS NULL OR EXISTS(SELECT 1 FROM learn_post_version_situations l JOIN learn_situations s ON s.id=l.situation_id WHERE l.post_version_id=lp.published_version_id AND s.slug=p_input->>'situation'))
   ORDER BY v.published_at DESC,lp.id OFFSET (page-1)*size LIMIT size) x;
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
 IF p_action='Bookmarks' THEN
  SELECT count(*) INTO total FROM learn_bookmarks WHERE trainee_user_id=p_actor;
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.created_at DESC,x.post_id),'[]') INTO result FROM (
   SELECT b.post_id,b.created_at,CASE WHEN lp.publication_status='Published' THEN jsonb_build_object('postId',lp.id,'available',true,'bookmarkedAt',b.created_at,'slug',lp.slug,'title',v.title,'summary',v.summary,'kind',v.content_kind)
    ELSE jsonb_build_object('postId',b.post_id,'available',false,'bookmarkedAt',b.created_at) END item
   FROM learn_bookmarks b JOIN learn_posts lp ON lp.id=b.post_id LEFT JOIN learn_post_versions v ON v.id=lp.published_version_id
   WHERE b.trainee_user_id=p_actor ORDER BY b.created_at DESC,b.post_id OFFSET (page-1)*size LIMIT size) x;
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

CREATE FUNCTION content_invalidate(p_scope text,p_id uuid,p_revision bigint,p_payload jsonb) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
BEGIN
 -- Cache invalidation through the existing outbox with its own aggregate; IFC processing dispatch never claims it.
 PERFORM enqueue_integration_outbox_event_internal(p_scope||':'||p_id||':'||p_revision,'Platform',p_id,'PlatformCacheInvalidation','1',
  jsonb_build_object('scope',p_scope,'id',p_id,'revision',p_revision)||p_payload,true,false);
END $$;

CREATE FUNCTION learn_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;p learn_posts;v learn_post_versions;src record;receipt content_command_receipts;h text;result jsonb;new_post uuid;new_version uuid;n integer;stamp timestamptz:=clock_timestamp();
 content jsonb;sid text;
BEGIN
 IF p_action NOT IN('CreateSituation','CreatePost','CreateVersion','UpdateVersion','PublishVersion','Hide','Show','Delete','Restore','List','Get','GetVersion') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='List' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object(
   'items',COALESCE((SELECT jsonb_agg(learn_post_representation(x.id,false) ORDER BY x.updated_at DESC,x.id) FROM (SELECT id,updated_at FROM learn_posts WHERE p_input->>'status' IS NULL OR publication_status=p_input->>'status' ORDER BY updated_at DESC,id OFFSET (COALESCE((p_input->>'page')::int,1)-1)*COALESCE((p_input->>'pageSize')::int,20) LIMIT COALESCE((p_input->>'pageSize')::int,20)) x),'[]'),
   'total',(SELECT count(*) FROM learn_posts WHERE p_input->>'status' IS NULL OR publication_status=p_input->>'status'),'page',COALESCE((p_input->>'page')::int,1),'pageSize',COALESCE((p_input->>'pageSize')::int,20)));
 ELSIF p_action='Get' THEN
  IF NOT EXISTS(SELECT 1 FROM learn_posts WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',learn_post_representation(p_resource,true));
 ELSIF p_action='GetVersion' THEN
  IF NOT EXISTS(SELECT 1 FROM learn_post_versions WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',learn_version_representation(p_resource));
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
  IF EXISTS(SELECT 1 FROM learn_situations WHERE slug=p_input->>'slug') THEN RETURN jsonb_build_object('code','SLUG_EXISTS','status',409);END IF;
  INSERT INTO learn_situations(id,slug,name,description,created_by,created_at,updated_at) VALUES(gen_random_uuid(),p_input->>'slug',btrim(p_input->>'name'),NULLIF(btrim(p_input->>'description'),''),p_actor,stamp,stamp) RETURNING jsonb_build_object('id',id,'slug',slug,'name',name,'description',description) INTO result;
  INSERT INTO content_command_receipts VALUES(p_actor,p_action,p_key,h,result,stamp);
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;

 IF p_action='CreatePost' THEN
  IF p_input->>'slug' !~ '^[a-z0-9]+(-[a-z0-9]+)*$' OR length(p_input->>'slug')>180 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
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

CREATE FUNCTION library_version_representation(p_version uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',v.id,'itemId',v.item_id,'kind',i.kind,'code',i.code,'versionNumber',v.version_number,'name',v.name,'payload',v.payload,
  'requiredCapabilities',v.required_capabilities,'payloadHash',v.payload_hash,'status',v.status,'publishedAt',v.published_at,'revision',v.revision,'createdAt',v.created_at,'updatedAt',v.updated_at)
 FROM organization_library_versions v JOIN organization_library_items i ON i.id=v.item_id WHERE v.id=p_version
$$;
CREATE FUNCTION library_item_representation(p_item uuid,p_admin boolean) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',i.id,'kind',i.kind,'code',i.code,'isActive',i.is_active,'revision',i.revision,'createdAt',i.created_at,'updatedAt',i.updated_at,
  'latestPublished',(SELECT library_version_representation(v.id) FROM organization_library_versions v WHERE v.item_id=i.id AND v.status='Published' ORDER BY v.version_number DESC LIMIT 1),
  'versions',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',v.id,'versionNumber',v.version_number,'name',v.name,'status',v.status,'revision',v.revision,'publishedAt',v.published_at) ORDER BY v.version_number DESC)
   FROM organization_library_versions v WHERE v.item_id=i.id AND (p_admin OR v.status='Published')),'[]'))
 FROM organization_library_items i WHERE i.id=p_item
$$;

CREATE FUNCTION library_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;i organization_library_items;v organization_library_versions;receipt content_command_receipts;h text;result jsonb;n integer;stamp timestamptz:=clock_timestamp();
 page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);total integer;admin boolean;
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
   SELECT count(*) INTO total FROM organization_library_items x WHERE (p_input->>'kind' IS NULL OR x.kind=p_input->>'kind') AND (admin OR (x.is_active AND EXISTS(SELECT 1 FROM organization_library_versions WHERE item_id=x.id AND status='Published')));
   SELECT COALESCE(jsonb_agg(library_item_representation(y.id,admin) ORDER BY y.code),'[]') INTO result FROM (SELECT x.id,x.code FROM organization_library_items x
    WHERE (p_input->>'kind' IS NULL OR x.kind=p_input->>'kind') AND (admin OR (x.is_active AND EXISTS(SELECT 1 FROM organization_library_versions WHERE item_id=x.id AND status='Published')))
    ORDER BY x.code OFFSET (page-1)*size LIMIT size) y;
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

-- Privileges.
DO $grants$ DECLARE t text;r text;sig text;s record;BEGIN
 FOREACH t IN ARRAY ARRAY['knowledge_sources','learn_situations','learn_posts','learn_post_versions','learn_post_version_situations','learn_post_version_sources','learn_bookmarks',
   'content_command_receipts','organization_library_items','organization_library_versions'] LOOP
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY content_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON %I FROM %I',t,r);END IF;END LOOP;
  FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON %I FROM %I',t,r);END IF;END LOOP;
 END LOOP;
 GRANT SELECT,INSERT,UPDATE ON knowledge_sources,learn_situations,learn_posts,learn_post_versions,organization_library_items,organization_library_versions TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT,DELETE ON learn_post_version_situations,learn_post_version_sources,learn_bookmarks TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT ON content_command_receipts TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['learn_version_representation(uuid)','learn_post_representation(uuid,boolean)','learn_rag_eligible(uuid,uuid)','learn_public_gate(text,uuid,uuid,jsonb)',
   'content_invalidate(text,uuid,bigint,jsonb)','learn_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','library_version_representation(uuid)','library_item_representation(uuid,boolean)','library_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION learn_public_gate(text,uuid,uuid,jsonb),learn_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint),library_gate(text,uuid,uuid,uuid,jsonb,text,bigint),learn_rag_eligible(uuid,uuid) TO %I',r);
 END IF;END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  FOREACH t IN ARRAY ARRAY['knowledge_sources','learn_situations','learn_posts','learn_post_versions','learn_bookmarks','content_command_receipts','organization_library_items','organization_library_versions'] LOOP
   EXECUTE format('GRANT SELECT ON %I TO fet3d_pending_cleanup_owner',t);
   EXECUTE format('CREATE POLICY pending_cleanup_owner ON %I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)',t);
  END LOOP;
 END IF;
 SELECT * INTO s FROM learn_library_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
