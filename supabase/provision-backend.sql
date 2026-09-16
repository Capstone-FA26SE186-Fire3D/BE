-- Optional: run once as migration owner after baseline+verify pass.
-- A missing password means this new login cannot password-authenticate yet.
-- Set its password privately with psql: \password fire3d_backend
-- Never grant migration/ledger-owner roles to this runtime login.
CREATE ROLE fire3d_backend LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
GRANT fire3d_api TO fire3d_backend;