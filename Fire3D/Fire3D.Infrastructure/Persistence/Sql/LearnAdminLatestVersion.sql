-- BE#154: the admin Learn list already names every post by its latest version (title, kind, number, status; #86), but not the
-- version id, so the CMS could not open the latest version of a post without a Draft (draftVersionId is null) from the list.
-- Adds 'latestVersionId'; body otherwise unchanged from ContentContractGaps.sql. Same signature and owner.
CREATE TEMP TABLE learn_admin_latest_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learn_admin_latest_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Admin list row: post state plus the latest version (any status, also for never-published or Deleted posts) and the open Draft.
-- The public title stays publishedVersion.title.
CREATE OR REPLACE FUNCTION learn_post_admin_summary(p_post uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT learn_post_representation(p.id,false)||jsonb_build_object(
  'title',lv.title,'kind',lv.content_kind,'latestVersionId',lv.id,'latestVersionNumber',lv.version_number,'latestVersionStatus',lv.status,
  'draftVersionId',dv.id,'draftVersionNumber',dv.version_number,'versionCount',(SELECT count(*) FROM learn_post_versions WHERE post_id=p.id))
 FROM learn_posts p
 LEFT JOIN LATERAL (SELECT * FROM learn_post_versions WHERE post_id=p.id ORDER BY version_number DESC LIMIT 1) lv ON true
 LEFT JOIN LATERAL (SELECT * FROM learn_post_versions WHERE post_id=p.id AND status='Draft' ORDER BY version_number DESC LIMIT 1) dv ON true
 WHERE p.id=p_post
$$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION learn_post_admin_summary(uuid) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION learn_post_admin_summary(uuid) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION learn_post_admin_summary(uuid) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM learn_admin_latest_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
