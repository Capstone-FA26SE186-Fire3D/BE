CREATE FUNCTION public.avatar_cleanup_gate(p_action text,p_key text DEFAULT NULL,p_id uuid DEFAULT NULL,
 p_lease uuid DEFAULT NULL,p_available timestamptz DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE job avatar_object_cleanups; protected boolean;
BEGIN
 IF p_action='Enqueue' THEN
  IF p_key IS NULL OR p_key NOT LIKE 'avatars/%' OR length(p_key)>2048 OR p_available IS NULL THEN
   RAISE EXCEPTION 'invalid avatar cleanup scope';
  END IF;
  INSERT INTO avatar_object_cleanups(id,object_key,available_at,attempts,created_at)
   VALUES(gen_random_uuid(),p_key,p_available,0,clock_timestamp()) ON CONFLICT(object_key) DO NOTHING;
  RETURN jsonb_build_object('code','OK');
 END IF;
 IF p_action='Claim' THEN
  -- Candidate grace accommodates ambiguous copy outcomes; it does not prove S3 stopped writing.
  INSERT INTO avatar_object_cleanups(id,object_key,available_at,attempts,created_at)
   SELECT gen_random_uuid(),candidate_object_key,clock_timestamp(),0,clock_timestamp()
   FROM avatar_upload_intents intent WHERE completed_at IS NULL AND candidate_object_key IS NOT NULL
    AND candidate_lease_until<=clock_timestamp()-interval '1 hour'
    AND NOT EXISTS(SELECT 1 FROM avatar_object_cleanups WHERE object_key=intent.candidate_object_key)
   ORDER BY candidate_lease_until,id LIMIT 100 ON CONFLICT(object_key) DO NOTHING;
  WITH candidate AS (
   SELECT id FROM avatar_object_cleanups WHERE available_at<=clock_timestamp()
    AND (lease_until IS NULL OR lease_until<=clock_timestamp())
    ORDER BY available_at,created_at,id FOR UPDATE SKIP LOCKED LIMIT 1)
  UPDATE avatar_object_cleanups cleanup SET lease_token=gen_random_uuid(),lease_until=clock_timestamp()+interval '2 minutes',attempts=attempts+1
   FROM candidate WHERE cleanup.id=candidate.id RETURNING cleanup.* INTO job;
  IF job.id IS NULL THEN RETURN jsonb_build_object('code','EMPTY'); END IF;
  RETURN jsonb_build_object('code','OK','id',job.id,'objectKey',job.object_key,'leaseToken',job.lease_token,'attempt',job.attempts);
 END IF;
 IF p_action='Referenced' THEN
  protected:=EXISTS(SELECT 1 FROM users WHERE avatar_storage_key=p_key)
   OR EXISTS(SELECT 1 FROM avatar_upload_intents WHERE completed_at IS NULL
    AND ((staging_object_key=p_key AND expires_at>clock_timestamp()-interval '1 hour')
      OR (candidate_object_key=p_key AND candidate_lease_until>clock_timestamp()-interval '1 hour')));
  RETURN jsonb_build_object('code','OK','referenced',protected);
 END IF;
 IF p_action NOT IN('Renew','Complete','Retry') THEN RAISE EXCEPTION 'invalid avatar cleanup action'; END IF;
 SELECT * INTO job FROM avatar_object_cleanups WHERE id=p_id AND lease_token=p_lease FOR UPDATE;
 IF job.id IS NULL OR job.lease_until<=clock_timestamp() THEN RETURN jsonb_build_object('code','STALE'); END IF;
 IF p_action='Renew' THEN
  UPDATE avatar_object_cleanups SET lease_until=clock_timestamp()+interval '2 minutes' WHERE id=job.id;
 ELSIF p_action='Complete' THEN
  protected:=(public.avatar_cleanup_gate('Referenced',job.object_key)->>'referenced')::boolean;
  IF protected THEN RETURN jsonb_build_object('code','PROTECTED'); END IF;
  DELETE FROM avatar_object_cleanups WHERE id=job.id;
 ELSE
  UPDATE avatar_object_cleanups SET available_at=clock_timestamp()+make_interval(secs=>least(900.0,power(2.0,least(job.attempts,6))*15)),
   lease_token=NULL,lease_until=NULL WHERE id=job.id;
 END IF;
 RETURN jsonb_build_object('code','OK');
END $$;

DO $permissions$
DECLARE os boolean;oi boolean;changed boolean;had_create boolean;t text;r text;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_avatar_cleanup_owner') THEN
  CREATE ROLE fet3d_avatar_cleanup_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
 END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_avatar_cleanup_owner'
  AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN RAISE EXCEPTION 'unsafe avatar cleanup owner'; END IF;
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_avatar_cleanup_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_avatar_cleanup_owner','SET') OR NOT pg_has_role(current_user,'fet3d_avatar_cleanup_owner','USAGE');
 had_create:=has_schema_privilege('fet3d_avatar_cleanup_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_avatar_cleanup_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_avatar_cleanup_owner;
 GRANT SELECT ON users,avatar_upload_intents TO fet3d_avatar_cleanup_owner;
 GRANT SELECT,INSERT,UPDATE,DELETE ON avatar_object_cleanups TO fet3d_avatar_cleanup_owner;
 FOREACH t IN ARRAY ARRAY['users','avatar_upload_intents'] LOOP
  EXECUTE format('CREATE POLICY avatar_cleanup_owner_read ON %I FOR SELECT TO fet3d_avatar_cleanup_owner USING(true)',t);
 END LOOP;
 CREATE POLICY avatar_cleanup_owner ON avatar_object_cleanups TO fet3d_avatar_cleanup_owner USING(true) WITH CHECK(true);
 ALTER FUNCTION public.avatar_cleanup_gate(text,text,uuid,uuid,timestamptz) OWNER TO fet3d_avatar_cleanup_owner;
 REVOKE ALL ON FUNCTION public.avatar_cleanup_gate(text,text,uuid,uuid,timestamptz) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('REVOKE ALL ON FUNCTION public.avatar_cleanup_gate(text,text,uuid,uuid,timestamptz) FROM %I',r);
  END IF;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('GRANT EXECUTE ON FUNCTION public.avatar_cleanup_gate(text,text,uuid,uuid,timestamptz) TO %I',r);
   EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON avatar_object_cleanups FROM %I',r);
  END IF;
 END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_avatar_cleanup_owner; END IF;
 IF changed THEN
  IF os IS NULL THEN EXECUTE format('REVOKE fet3d_avatar_cleanup_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_avatar_cleanup_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $permissions$;
