-- BE#171: images a PlatformAdmin uploads for Learn covers and image blocks. The bytes stay in private S3: an intent names a staging
-- object the admin PUTs to; complete checks the bytes and copies them to learn/images/{id}, which the API serves at a stable public
-- address by redirecting to a short-lived signed URL. A completed row never changes (published Learn versions may reference it).
CREATE TABLE IF NOT EXISTS public.learn_media_images(
 id uuid PRIMARY KEY,
 created_by uuid NOT NULL REFERENCES public.users(id),
 idempotency_key varchar(128) NOT NULL CHECK (idempotency_key ~ '^[!-~]{1,128}$'),
 input_hash char(64) NOT NULL CHECK (input_hash ~ '^[0-9a-f]{64}$'),
 file_name varchar(255) NOT NULL CHECK (length(btrim(file_name)) BETWEEN 1 AND 255),
 content_type text NOT NULL CHECK (content_type IN ('image/jpeg','image/png','image/webp')),
 size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 5242880),
 sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
 staging_key text NOT NULL UNIQUE,
 object_key text UNIQUE,
 width integer CHECK (width BETWEEN 1 AND 4096),
 height integer CHECK (height BETWEEN 1 AND 4096),
 created_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
 completed_at timestamptz,
 CONSTRAINT learn_media_images_idempotency UNIQUE (created_by, idempotency_key),
 CONSTRAINT learn_media_images_completion CHECK (
  (completed_at IS NULL) = (object_key IS NULL) AND (completed_at IS NULL) = (width IS NULL) AND (completed_at IS NULL) = (height IS NULL))
);

-- A completed image is immutable: only the completing update may set its object, and nothing changes it afterwards.
-- The API role has no DELETE grant, so a published image cannot disappear either.
CREATE OR REPLACE FUNCTION public.immutable_learn_media_image() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
 IF OLD.completed_at IS NOT NULL OR NEW.id<>OLD.id OR NEW.created_by<>OLD.created_by OR NEW.idempotency_key<>OLD.idempotency_key
  OR NEW.input_hash<>OLD.input_hash OR NEW.file_name<>OLD.file_name OR NEW.content_type<>OLD.content_type OR NEW.size_bytes<>OLD.size_bytes
  OR NEW.sha256<>OLD.sha256 OR NEW.staging_key<>OLD.staging_key OR NEW.created_at<>OLD.created_at OR NEW.expires_at<>OLD.expires_at THEN
  RAISE EXCEPTION 'learn media image % is immutable once created or completed', OLD.id USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS immutable_learn_media_image ON public.learn_media_images;
CREATE TRIGGER immutable_learn_media_image BEFORE UPDATE ON public.learn_media_images FOR EACH ROW
 EXECUTE FUNCTION public.immutable_learn_media_image();
REVOKE ALL ON FUNCTION public.immutable_learn_media_image() FROM PUBLIC;

ALTER TABLE public.learn_media_images ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.learn_media_images FROM PUBLIC;
DO $$
BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN REVOKE ALL ON TABLE public.learn_media_images FROM anon; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN REVOKE ALL ON TABLE public.learn_media_images FROM authenticated; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
  GRANT SELECT,INSERT,UPDATE ON TABLE public.learn_media_images TO fire3d_api;
  DROP POLICY IF EXISTS fire3d_api_learn_media ON public.learn_media_images;
  CREATE POLICY fire3d_api_learn_media ON public.learn_media_images TO fire3d_api USING (true) WITH CHECK (true);
 END IF;
 -- Pending-registration cleanup deletes users; the foreign key check reads this table as that owner, like every other users child.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON TABLE public.learn_media_images TO fet3d_pending_cleanup_owner;
  DROP POLICY IF EXISTS pending_cleanup_owner ON public.learn_media_images;
  CREATE POLICY pending_cleanup_owner ON public.learn_media_images TO fet3d_pending_cleanup_owner USING (true) WITH CHECK (true);
 END IF;
END $$;
