-- Preserve legacy Building writes without granting writes to access fields.
DO $acl$ DECLARE grant_row record; recipient text; columns_sql text; t text; sig text; client text; os boolean; oi boolean; changed boolean;
BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 SELECT string_agg(quote_ident(attname),',' ORDER BY attnum) INTO columns_sql FROM pg_attribute
 WHERE attrelid='public.buildings'::regclass AND attnum>0 AND NOT attisdropped
 AND attname NOT IN('visibility','access_revision','participation_code_hash');
 FOR grant_row IN SELECT DISTINCT a.grantee,a.privilege_type FROM pg_class c
 CROSS JOIN LATERAL aclexplode(c.relacl) a LEFT JOIN pg_roles r ON r.oid=a.grantee
 WHERE c.oid='public.buildings'::regclass AND a.privilege_type IN('INSERT','UPDATE')
 AND a.grantee<>c.relowner AND a.grantee<>'fet3d_ifc_upload_owner'::regrole
 AND NOT COALESCE(r.rolsuper,false) LOOP
  recipient:=CASE WHEN grant_row.grantee=0 THEN 'PUBLIC' ELSE quote_ident(grant_row.grantee::regrole::text) END;
  EXECUTE format('REVOKE %s ON public.buildings FROM %s',grant_row.privilege_type,recipient);
  EXECUTE format('GRANT %s (%s) ON public.buildings TO %s',grant_row.privilege_type,columns_sql,recipient);
 END LOOP;
 -- Remove any pre-existing column-level access grants as well.
 FOR grant_row IN SELECT DISTINCT a.grantee FROM pg_attribute c
 CROSS JOIN LATERAL aclexplode(c.attacl) a LEFT JOIN pg_roles r ON r.oid=a.grantee
 WHERE c.attrelid='public.buildings'::regclass AND c.attname IN('visibility','access_revision','participation_code_hash')
 AND a.privilege_type IN('INSERT','UPDATE') AND a.grantee<>(SELECT relowner FROM pg_class WHERE oid=c.attrelid)
 AND a.grantee<>'fet3d_ifc_upload_owner'::regrole AND NOT COALESCE(r.rolsuper,false) LOOP
  recipient:=CASE WHEN grant_row.grantee=0 THEN 'PUBLIC' ELSE quote_ident(grant_row.grantee::regrole::text) END;
  EXECUTE format('REVOKE INSERT (visibility,access_revision,participation_code_hash),UPDATE (visibility,access_revision,participation_code_hash) ON public.buildings FROM %s',recipient);
 END LOOP;
 -- Custom runtime logins can have direct grants in addition to role membership.
 FOREACH t IN ARRAY ARRAY['releases','release_packages','trainings','building_participation_grants','release_build_provenance','release_command_receipts','feedback','support_tickets','support_ticket_messages','support_command_receipts'] LOOP
  FOR grant_row IN SELECT DISTINCT a.grantee FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
  LEFT JOIN pg_roles r ON r.oid=a.grantee WHERE c.oid=format('public.%I',t)::regclass
  AND a.privilege_type IN('INSERT','UPDATE','DELETE','TRUNCATE') AND a.grantee<>c.relowner
  AND a.grantee<>'fet3d_ifc_upload_owner'::regrole AND NOT COALESCE(r.rolsuper,false) LOOP
   recipient:=CASE WHEN grant_row.grantee=0 THEN 'PUBLIC' ELSE quote_ident(grant_row.grantee::regrole::text) END;
   EXECUTE format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON public.%I FROM %s',t,recipient);
  END LOOP;
 END LOOP;
 -- Supabase defaults under another migration owner must not expose these gates.
 FOREACH sig IN ARRAY ARRAY['release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','release_representation(uuid)','support_command_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','support_feedback_representation(uuid)','support_ticket_representation(uuid)'] LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION public.%s FROM PUBLIC',sig);
  FOREACH client IN ARRAY ARRAY['anon','authenticated'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client) THEN EXECUTE format('REVOKE ALL ON FUNCTION public.%s FROM %I',sig,client);END IF;
  END LOOP;
 END LOOP;
 IF changed THEN
  IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END);END IF;
 END IF;
END $acl$;

-- Defense against future accidental table-wide Building grants. Invoker identity
-- is the gate owner during SECURITY DEFINER execution, not the API login.
CREATE FUNCTION guard_building_access_writer() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN
 IF (TG_OP='INSERT' AND (NEW.visibility<>'Private' OR NEW.access_revision<>1 OR NEW.participation_code_hash IS NOT NULL))
 OR (TG_OP='UPDATE' AND (NEW.visibility IS DISTINCT FROM OLD.visibility OR NEW.access_revision IS DISTINCT FROM OLD.access_revision OR NEW.participation_code_hash IS DISTINCT FROM OLD.participation_code_hash)) THEN
  IF current_user<>'fet3d_ifc_upload_owner' AND current_user<>(SELECT relowner::regrole::text FROM pg_class WHERE oid=TG_RELID)
  AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND rolsuper) THEN
   RAISE EXCEPTION 'Building access requires the authorized gate' USING ERRCODE='42501';
  END IF;
 END IF;
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION guard_building_access_writer() FROM PUBLIC;
CREATE TRIGGER building_access_writer BEFORE INSERT OR UPDATE ON buildings FOR EACH ROW EXECUTE FUNCTION guard_building_access_writer();
