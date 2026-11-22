-- Avatar cleanup loop: Complete keeps a done marker for the candidate of an abandoned upload intent instead of deleting the row, so
-- Claim stops queueing the same object again. avatar_cleanup_gate keeps its signature and the body of AvatarCleanupGate otherwise,
-- so owner (fet3d_avatar_cleanup_owner), search_path and EXECUTE grants are unchanged by OR REPLACE. The owner already has UPDATE on
-- avatar_object_cleanups and SELECT on avatar_upload_intents; no grant changes.
CREATE TEMP TABLE avatar_cleanup_loop_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_avatar_cleanup_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_avatar_cleanup_owner','SET') OR NOT pg_has_role(current_user,'fet3d_avatar_cleanup_owner','USAGE');
 INSERT INTO avatar_cleanup_loop_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_avatar_cleanup_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;

CREATE OR REPLACE FUNCTION public.avatar_cleanup_gate(p_action text,p_key text DEFAULT NULL,p_id uuid DEFAULT NULL,
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
  -- An abandoned upload intent keeps naming its candidate, and Claim queues every such candidate that has no row here. Deleting
  -- the row let the next Claim queue the same object at once, so the worker looped without pause (about 46 million gate calls
  -- on production). For such a key the row stays as a done marker that Claim never picks (available_at is infinity).
  IF EXISTS(SELECT 1 FROM avatar_upload_intents WHERE completed_at IS NULL AND candidate_object_key=job.object_key) THEN
   UPDATE avatar_object_cleanups SET available_at='infinity',lease_token=NULL,lease_until=NULL WHERE id=job.id;
  ELSE
   DELETE FROM avatar_object_cleanups WHERE id=job.id;
  END IF;
 ELSE
  UPDATE avatar_object_cleanups SET available_at=clock_timestamp()+make_interval(secs=>least(900.0,power(2.0,least(job.attempts,6))*15)),
   lease_token=NULL,lease_until=NULL WHERE id=job.id;
 END IF;
 RETURN jsonb_build_object('code','OK');
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM avatar_cleanup_loop_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_avatar_cleanup_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_avatar_cleanup_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
