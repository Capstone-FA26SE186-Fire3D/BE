-- /health/ready compares the build's migrations with __EFMigrationsHistory. The runtime login (member of fire3d_api) could not
-- read that table, so readiness failed with permission denied on a fully migrated database. Migration IDs are not sensitive;
-- only SELECT is granted.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO fire3d_api;END IF;
END $$;
