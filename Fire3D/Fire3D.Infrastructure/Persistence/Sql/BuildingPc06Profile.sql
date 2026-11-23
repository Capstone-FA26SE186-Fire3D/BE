-- BE#212 (Flow 1 PC06, design §2): a Building is an Apartment or a School and carries the facility information of Mẫu PC06 part II.
-- * building_type: legacy free text is mapped ("Chung cư" -> Apartment, "Trường học" -> School), anything else becomes NULL and the
--   API requires a choice on the next edit; a CHECK keeps only the two values from now on.
-- * New facility columns: floor area, head of facility and phone, on-site force, emergency contacts, the signed PC06 plan (PDF).
--   riskGroup (Phụ lục II Nghị định 105/2025/NĐ-CP) is computed by the API from type, total_floors and floor area; never stored.
-- * building_plan_documents: private PDF uploads (presigned PUT to staging, checked and copied on complete), immutable once stored.
UPDATE public.buildings SET building_type=CASE
  WHEN lower(btrim(building_type)) IN('apartment','chung cư','chung cu','nhà chung cư','nha chung cu') THEN 'Apartment'
  WHEN lower(btrim(building_type)) IN('school','trường học','truong hoc','trường','truong') THEN 'School'
  ELSE NULL END
WHERE building_type IS NOT NULL AND building_type NOT IN('Apartment','School');
ALTER TABLE public.buildings ADD CONSTRAINT buildings_type_pc06 CHECK (building_type IS NULL OR building_type IN('Apartment','School'));

CREATE TABLE public.building_plan_documents(
 id uuid PRIMARY KEY,
 building_id uuid NOT NULL REFERENCES public.buildings(id),
 created_by uuid NOT NULL REFERENCES public.users(id),
 idempotency_key varchar(128) NOT NULL CHECK (idempotency_key ~ '^[!-~]{1,128}$'),
 input_hash char(64) NOT NULL CHECK (input_hash ~ '^[0-9a-f]{64}$'),
 file_name varchar(255) NOT NULL CHECK (length(btrim(file_name)) BETWEEN 1 AND 255),
 content_type text NOT NULL CHECK (content_type='application/pdf'),
 size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 20971520),
 staging_key text NOT NULL UNIQUE,
 object_key text UNIQUE,
 sha256 char(64) CHECK (sha256 ~ '^[0-9a-f]{64}$'),
 created_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
 completed_at timestamptz,
 CONSTRAINT building_plan_documents_idempotency UNIQUE (created_by, idempotency_key),
 CONSTRAINT building_plan_documents_completion CHECK ((completed_at IS NULL) = (object_key IS NULL) AND (completed_at IS NULL) = (sha256 IS NULL))
);
CREATE INDEX building_plan_documents_building ON public.building_plan_documents(building_id,created_at DESC);

CREATE FUNCTION public.immutable_building_plan_document() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
 IF TG_OP='DELETE' OR NEW.id<>OLD.id OR NEW.building_id<>OLD.building_id OR NEW.created_by<>OLD.created_by OR NEW.idempotency_key<>OLD.idempotency_key
  OR NEW.input_hash<>OLD.input_hash OR NEW.file_name<>OLD.file_name OR NEW.content_type<>OLD.content_type OR NEW.size_bytes<>OLD.size_bytes
  OR NEW.staging_key<>OLD.staging_key OR NEW.created_at<>OLD.created_at OR NEW.expires_at<>OLD.expires_at
  OR (OLD.completed_at IS NOT NULL AND (NEW.object_key IS DISTINCT FROM OLD.object_key OR NEW.sha256 IS DISTINCT FROM OLD.sha256
   OR NEW.completed_at IS DISTINCT FROM OLD.completed_at)) THEN
  RAISE EXCEPTION 'building plan document % is immutable once stored', OLD.id USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER immutable_building_plan_document BEFORE UPDATE OR DELETE ON public.building_plan_documents FOR EACH ROW EXECUTE FUNCTION public.immutable_building_plan_document();
REVOKE ALL ON FUNCTION public.immutable_building_plan_document() FROM PUBLIC;

ALTER TABLE public.buildings
 ADD COLUMN floor_area_square_meters numeric(12,2) CHECK (floor_area_square_meters IS NULL OR floor_area_square_meters > 0),
 ADD COLUMN facility_head_name varchar(200),
 ADD COLUMN facility_head_phone varchar(32),
 ADD COLUMN on_site_force jsonb CHECK (on_site_force IS NULL OR jsonb_typeof(on_site_force)='object'),
 ADD COLUMN emergency_contacts jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(emergency_contacts)='array'),
 ADD COLUMN signed_plan_document_id uuid REFERENCES public.building_plan_documents(id);

ALTER TABLE public.building_plan_documents ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.building_plan_documents FROM PUBLIC;
DO $$
DECLARE owner text;
BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN REVOKE ALL ON TABLE public.building_plan_documents FROM anon; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN REVOKE ALL ON TABLE public.building_plan_documents FROM authenticated; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
  GRANT SELECT,INSERT,UPDATE ON TABLE public.building_plan_documents TO fire3d_api;
  CREATE POLICY fire3d_api_building_plan_documents ON public.building_plan_documents TO fire3d_api USING (true) WITH CHECK (true);
  -- Building writes stay column-scoped (RuntimeCoreGrants): the new facility columns join the writable set.
  GRANT INSERT(floor_area_square_meters,facility_head_name,facility_head_phone,on_site_force,emergency_contacts,signed_plan_document_id),
   UPDATE(floor_area_square_meters,facility_head_name,facility_head_phone,on_site_force,emergency_contacts,signed_plan_document_id) ON TABLE public.buildings TO fire3d_api;
 END IF;
 -- Cleanup owners read every child of users/buildings for their foreign key checks, as for the other user children.
 FOREACH owner IN ARRAY ARRAY['fet3d_pending_cleanup_owner','fet3d_avatar_cleanup_owner','fet3d_password_recovery_owner'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=owner) THEN
   EXECUTE format('GRANT SELECT ON TABLE public.building_plan_documents TO %I',owner);
   EXECUTE format('CREATE POLICY %I ON public.building_plan_documents TO %I USING (true) WITH CHECK (true)',replace(owner,'fet3d_','')||'_plan_documents',owner);
  END IF;
 END LOOP;
END $$;
