-- Enterprise request workflow: Admin assignment, append-only responses visible to the requesting organization, and a Closed
-- state that ends handling without touching any quotation or payment. Additive; existing requests keep their status.
DO $preflight$ BEGIN
 IF EXISTS(SELECT 1 FROM enterprise_quote_requests WHERE status NOT IN('New','Contacted','Quoted','Accepted','Rejected','Cancelled')) THEN
  RAISE EXCEPTION 'Enterprise workflow preflight: unknown request status requires operator review'; END IF;
END $preflight$;
ALTER TABLE enterprise_quote_requests ADD COLUMN IF NOT EXISTS assigned_to uuid REFERENCES users(id),
 ADD COLUMN IF NOT EXISTS closed_at timestamptz,ADD COLUMN IF NOT EXISTS closed_by uuid REFERENCES users(id),ADD COLUMN IF NOT EXISTS close_reason text;
ALTER TABLE enterprise_quote_requests DROP CONSTRAINT IF EXISTS check_enterprise_request_status;
ALTER TABLE enterprise_quote_requests ADD CONSTRAINT check_enterprise_request_status CHECK(status IN('New','Contacted','Quoted','Accepted','Rejected','Cancelled','Closed'));
ALTER TABLE enterprise_quote_requests DROP CONSTRAINT IF EXISTS check_enterprise_request_closed;
ALTER TABLE enterprise_quote_requests ADD CONSTRAINT check_enterprise_request_closed CHECK(
 (status='Closed')=(closed_at IS NOT NULL AND closed_by IS NOT NULL AND close_reason IS NOT NULL) AND (close_reason IS NULL OR length(btrim(close_reason)) BETWEEN 1 AND 1000));

CREATE TABLE IF NOT EXISTS enterprise_quote_responses(
 id uuid PRIMARY KEY,request_id uuid NOT NULL REFERENCES enterprise_quote_requests(id),author_id uuid NOT NULL REFERENCES users(id),
 message text NOT NULL CHECK(length(btrim(message)) BETWEEN 1 AND 10000),created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS enterprise_quote_responses_page ON enterprise_quote_responses(request_id,created_at,id);
CREATE OR REPLACE FUNCTION append_only_enterprise_response() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Enterprise responses are append-only' USING ERRCODE='23514'; END $$;
DROP TRIGGER IF EXISTS append_only_enterprise_response ON enterprise_quote_responses;
CREATE TRIGGER append_only_enterprise_response BEFORE UPDATE OR DELETE ON enterprise_quote_responses FOR EACH ROW EXECUTE FUNCTION append_only_enterprise_response();
REVOKE ALL ON enterprise_quote_responses FROM PUBLIC;
DO $grants$ DECLARE r text;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON enterprise_quote_responses FROM %I',r);END IF;END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN GRANT SELECT,INSERT ON enterprise_quote_responses TO fire3d_api;END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN GRANT SELECT ON enterprise_quote_responses TO fet3d_pending_cleanup_owner;END IF;
END $grants$;
