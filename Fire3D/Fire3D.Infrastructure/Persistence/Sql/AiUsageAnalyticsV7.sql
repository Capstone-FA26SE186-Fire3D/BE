-- BE#117: analytics_ai_usage still counted requests under the pre-v7 status names. AddAiContractV2 moved every ai_requests row to
-- Accepted/Processing/Succeeded/Failed/NeedsReconcile/Rejected (InsufficientEvidence and SafetyRejected became Succeeded + result_type), so
-- Queued/Running/InsufficientEvidence/SafetyRejected were always 0 and waiting/processing requests were never counted.
-- Now: requests by v7 status; resultTypes splits Succeeded requests by result_type; the platform view (no organization) adds
-- byAudience (organization/trainee). Same signature, owner and callers (analytics_gate, admin organization and training v2 reports).
CREATE TEMP TABLE ai_usage_v7_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_usage_v7_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION analytics_ai_usage(p_org uuid,p_from timestamptz,p_to timestamptz,p_as_of timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH scoped AS (SELECT r.status,r.result_type,r.audience FROM ai_requests r
   WHERE r.created_at>=p_from AND r.created_at<p_to AND r.created_at<=p_as_of AND (p_org IS NULL OR r.organization_id=p_org)),
  statuses AS (SELECT * FROM unnest(ARRAY['Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected']) WITH ORDINALITY s(status,ord))
 SELECT jsonb_build_object(
  'settledUnits',COALESCE((SELECT jsonb_agg(jsonb_build_object('quotaUnit',x.quota_unit,'units',x.units) ORDER BY x.quota_unit) FROM (
    SELECT a.quota_unit,sum(a.consumed_units)::bigint units FROM billing_ai_quota_allocations a
    WHERE a.status='Settled' AND a.settled_at>=p_from AND a.settled_at<p_to AND a.settled_at<=p_as_of AND (p_org IS NULL OR a.organization_id=p_org) GROUP BY a.quota_unit) x),'[]'),
  'openReservations',COALESCE((SELECT jsonb_agg(jsonb_build_object('quotaUnit',x.quota_unit,'units',x.units) ORDER BY x.quota_unit) FROM (
    SELECT a.quota_unit,sum(a.reserved_units)::bigint units FROM billing_ai_quota_allocations a
    WHERE a.status='Reserved' AND (p_org IS NULL OR a.organization_id=p_org) GROUP BY a.quota_unit) x),'[]'),
  'needsReconcile',(SELECT jsonb_build_object('requests',count(*),'reservedUnits',COALESCE(sum(r.reserved_units),0)) FROM ai_requests r WHERE r.status='NeedsReconcile' AND (p_org IS NULL OR r.organization_id=p_org)),
  'requests',(SELECT jsonb_object_agg(s.status,(SELECT count(*) FROM scoped c WHERE c.status=s.status) ORDER BY s.ord) FROM statuses s),
  'resultTypes',(SELECT jsonb_object_agg(t.result_type,(SELECT count(*) FROM scoped c WHERE c.status='Succeeded' AND c.result_type=t.result_type) ORDER BY t.ord)
    FROM unnest(ARRAY['KnowledgeAnswer','ScenarioDraft','InsufficientEvidence','RejectedBySafetyGate']) WITH ORDINALITY t(result_type,ord)),
  -- Trainee requests have no organization, so only the platform view can show them; an organization view counts its own requests.
  'byAudience',CASE WHEN p_org IS NULL THEN (SELECT jsonb_object_agg(a.audience,(SELECT jsonb_object_agg(s.status,(SELECT count(*) FROM scoped c WHERE c.audience=a.audience AND c.status=s.status) ORDER BY s.ord) FROM statuses s))
    FROM unnest(ARRAY['organization','trainee']) a(audience)) END)
$$;

DO $grants$ DECLARE r text;s record;sig text:='analytics_ai_usage(uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone)';BEGIN
 EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 SELECT * INTO s FROM ai_usage_v7_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
