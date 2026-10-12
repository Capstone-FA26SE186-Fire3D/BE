-- Revoking a release archives its Trainings (#51). Access was already cut by the Published filter on lists and session
-- launch; archiving makes the withdrawn state explicit to every reader. Building participation grants belong to the
-- Building, not the release, and are left unchanged. Already-started sessions keep their own lifecycle.
CREATE TEMP TABLE release_revoke_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO release_revoke_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;
GRANT UPDATE(status,updated_at) ON trainings TO fet3d_ifc_upload_owner;

CREATE FUNCTION archive_revoked_release_trainings() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
BEGIN
 UPDATE trainings SET status='Archived',updated_at=now() WHERE release_id=NEW.id AND status<>'Archived';
 RETURN NULL;
END $$;

CREATE TRIGGER archive_revoked_release_trainings AFTER UPDATE OF status ON releases
 FOR EACH ROW WHEN (NEW.status::text='Revoked' AND OLD.status IS DISTINCT FROM NEW.status)
 EXECUTE FUNCTION archive_revoked_release_trainings();

-- Releases revoked before this migration get the same explicit state.
UPDATE trainings t SET status='Archived',updated_at=now() FROM releases r WHERE r.id=t.release_id AND r.status::text='Revoked' AND t.status<>'Archived';

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION archive_revoked_release_trainings() OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION archive_revoked_release_trainings() FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION archive_revoked_release_trainings() FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM release_revoke_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
