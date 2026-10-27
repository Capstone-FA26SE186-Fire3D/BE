-- BE#179: a PlatformAdmin registers a knowledge source by uploading the document instead of typing a URL and its SHA-256.
-- An intent names a private staging object the admin PUTs to; complete reads the stored bytes, computes the SHA-256 itself,
-- copies them to knowledge/files/{id} and registers the source (Draft) with sourceUri = the API's stable address for that file,
-- which redirects to a short-lived signed URL. The AI service fetches sourceUri and compares the hash, so a stored file must
-- never change: a new version of a document is a new upload and a new source version. Files stay readable after the source is
-- retired (audit); the API role has no DELETE grant.
CREATE TABLE IF NOT EXISTS public.knowledge_source_files(
 id uuid PRIMARY KEY,
 created_by uuid NOT NULL REFERENCES public.users(id),
 idempotency_key varchar(128) NOT NULL CHECK (idempotency_key ~ '^[!-~]{1,128}$'),
 input_hash char(64) NOT NULL CHECK (input_hash ~ '^[0-9a-f]{64}$'),
 file_name varchar(255) NOT NULL CHECK (length(btrim(file_name)) BETWEEN 1 AND 255),
 content_type text NOT NULL CHECK (content_type IN ('application/pdf','text/markdown','text/plain','text/html')),
 size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 20971520),
 staging_key text NOT NULL UNIQUE,
 object_key text UNIQUE,
 sha256 char(64) CHECK (sha256 ~ '^[0-9a-f]{64}$'),
 source_id uuid UNIQUE REFERENCES public.knowledge_sources(id),
 created_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
 completed_at timestamptz,
 CONSTRAINT knowledge_source_files_idempotency UNIQUE (created_by, idempotency_key),
 CONSTRAINT knowledge_source_files_completion CHECK ((completed_at IS NULL) = (object_key IS NULL) AND (completed_at IS NULL) = (sha256 IS NULL)),
 CONSTRAINT knowledge_source_files_source CHECK (source_id IS NULL OR completed_at IS NOT NULL)
);

-- The stored file is immutable: the completing update sets object, hash and time once; afterwards only the link to the source it
-- registered may be set, once.
CREATE OR REPLACE FUNCTION public.immutable_knowledge_source_file() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
 IF NEW.id<>OLD.id OR NEW.created_by<>OLD.created_by OR NEW.idempotency_key<>OLD.idempotency_key OR NEW.input_hash<>OLD.input_hash
  OR NEW.file_name<>OLD.file_name OR NEW.content_type<>OLD.content_type OR NEW.size_bytes<>OLD.size_bytes OR NEW.staging_key<>OLD.staging_key
  OR NEW.created_at<>OLD.created_at OR NEW.expires_at<>OLD.expires_at
  OR (OLD.completed_at IS NOT NULL AND (NEW.object_key IS DISTINCT FROM OLD.object_key OR NEW.sha256 IS DISTINCT FROM OLD.sha256
   OR NEW.completed_at IS DISTINCT FROM OLD.completed_at))
  OR (OLD.source_id IS NOT NULL AND NEW.source_id IS DISTINCT FROM OLD.source_id) THEN
  RAISE EXCEPTION 'knowledge source file % is immutable once stored', OLD.id USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS immutable_knowledge_source_file ON public.knowledge_source_files;
CREATE TRIGGER immutable_knowledge_source_file BEFORE UPDATE ON public.knowledge_source_files FOR EACH ROW
 EXECUTE FUNCTION public.immutable_knowledge_source_file();
REVOKE ALL ON FUNCTION public.immutable_knowledge_source_file() FROM PUBLIC;

ALTER TABLE public.knowledge_source_files ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.knowledge_source_files FROM PUBLIC;
DO $$
BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN REVOKE ALL ON TABLE public.knowledge_source_files FROM anon; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN REVOKE ALL ON TABLE public.knowledge_source_files FROM authenticated; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
  GRANT SELECT,INSERT,UPDATE ON TABLE public.knowledge_source_files TO fire3d_api;
  DROP POLICY IF EXISTS fire3d_api_knowledge_source_files ON public.knowledge_source_files;
  CREATE POLICY fire3d_api_knowledge_source_files ON public.knowledge_source_files TO fire3d_api USING (true) WITH CHECK (true);
 END IF;
 -- Pending-registration cleanup deletes users; the foreign key check reads this table as that owner, like every other users child.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON TABLE public.knowledge_source_files TO fet3d_pending_cleanup_owner;
  DROP POLICY IF EXISTS pending_cleanup_owner ON public.knowledge_source_files;
  CREATE POLICY pending_cleanup_owner ON public.knowledge_source_files TO fet3d_pending_cleanup_owner USING (true) WITH CHECK (true);
 END IF;
END $$;
