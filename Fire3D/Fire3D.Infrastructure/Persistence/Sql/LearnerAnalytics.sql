-- Training, revenue and AI usage analytics over server-owned facts. Read-only; additive; nothing is seeded.
-- The caller runs this inside one READ ONLY RepeatableRead transaction; asOf is the transaction timestamp.
CREATE TEMP TABLE learner_analytics_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learner_analytics_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE INDEX IF NOT EXISTS session_results_session_created ON session_results(session_id,created_at);
CREATE INDEX IF NOT EXISTS sessions_v7_running_heartbeat ON sessions(last_heartbeat_at) WHERE contract_version=7 AND status='Running';

-- AI accounting for one scope (NULL organization = platform): settled usage in range, open reservations and reconciliation backlog now.
CREATE FUNCTION analytics_ai_usage(p_org uuid,p_from timestamptz,p_to timestamptz,p_as_of timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object(
  'settledUnits',COALESCE((SELECT jsonb_agg(jsonb_build_object('quotaUnit',x.quota_unit,'units',x.units) ORDER BY x.quota_unit) FROM (
    SELECT a.quota_unit,sum(a.consumed_units)::bigint units FROM billing_ai_quota_allocations a
    WHERE a.status='Settled' AND a.settled_at>=p_from AND a.settled_at<p_to AND a.settled_at<=p_as_of AND (p_org IS NULL OR a.organization_id=p_org) GROUP BY a.quota_unit) x),'[]'),
  'openReservations',COALESCE((SELECT jsonb_agg(jsonb_build_object('quotaUnit',x.quota_unit,'units',x.units) ORDER BY x.quota_unit) FROM (
    SELECT a.quota_unit,sum(a.reserved_units)::bigint units FROM billing_ai_quota_allocations a
    WHERE a.status='Reserved' AND (p_org IS NULL OR a.organization_id=p_org) GROUP BY a.quota_unit) x),'[]'),
  'needsReconcile',(SELECT jsonb_build_object('requests',count(*),'reservedUnits',COALESCE(sum(r.reserved_units),0)) FROM ai_requests r WHERE r.status='NeedsReconcile' AND (p_org IS NULL OR r.organization_id=p_org)),
  'requests',(SELECT jsonb_object_agg(s.status,COALESCE(c.n,0)) FROM unnest(ARRAY['Queued','Running','Succeeded','InsufficientEvidence','SafetyRejected','Failed','NeedsReconcile']) s(status)
    LEFT JOIN (SELECT r.status,count(*) n FROM ai_requests r WHERE r.created_at>=p_from AND r.created_at<p_to AND (p_org IS NULL OR r.organization_id=p_org) GROUP BY r.status) c ON c.status=s.status))
$$;

-- Learner training metrics for the cohort of contract-7 sessions started in [from,to). Playtests live in their own tables and
-- preparations are not sessions, so neither is counted. A late offline result updates the cohort of the session's start.
CREATE FUNCTION analytics_training(p_org uuid,p_from timestamptz,p_to timestamptz,p_as_of timestamptz,p_active_seconds integer) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH cohort AS (
  SELECT s.id,s.organization_id,s.training_id,s.trainee_user_id,s.mode::text mode,s.launched_at,s.ended_at,r.outcome,r.created_at result_at
  FROM sessions s LEFT JOIN LATERAL (SELECT x.outcome,x.created_at FROM session_results x WHERE x.session_id=s.id AND x.contract_version=7 AND x.created_at<=p_as_of ORDER BY x.created_at,x.id LIMIT 1) r ON true
  WHERE s.contract_version=7 AND s.started_at>=p_from AND s.started_at<p_to AND s.started_at<=p_as_of AND (p_org IS NULL OR s.organization_id=p_org)),
 totals AS (SELECT count(*) plays,count(DISTINCT trainee_user_id) trainees,count(result_at) completed,
   count(*) FILTER(WHERE outcome IN('Passed','NotPassed')) assessed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort),
 durations AS (SELECT extract(epoch FROM ended_at-launched_at) seconds FROM cohort WHERE result_at IS NOT NULL AND launched_at IS NOT NULL AND ended_at IS NOT NULL AND ended_at<=p_as_of AND ended_at>=launched_at)
 SELECT jsonb_build_object(
  'plays',t.plays,'uniqueTrainees',t.trainees,'completed',t.completed,
  'completionRate',CASE WHEN t.plays>0 THEN round(t.completed::numeric/t.plays,4) END,
  'activeSessions',(SELECT count(*) FROM sessions s WHERE s.contract_version=7 AND s.status='Running' AND s.last_heartbeat_at>p_as_of-make_interval(secs=>p_active_seconds)
    AND s.last_heartbeat_at<=p_as_of AND (p_org IS NULL OR s.organization_id=p_org)),
  'outcomes',(SELECT jsonb_object_agg(o.outcome,(SELECT count(*) FROM cohort c WHERE c.outcome=o.outcome)) FROM unnest(ARRAY['Passed','NotPassed','Incomplete','NotAssessed']) o(outcome)),
  'assessment',jsonb_build_object('assessed',t.assessed,'passed',t.passed,'passRate',CASE WHEN t.assessed>0 THEN round(t.passed::numeric/t.assessed,4) END),
  'duration',(SELECT jsonb_build_object('sessions',count(*),'averageSeconds',round(avg(seconds)::numeric,1),'medianSeconds',round((percentile_cont(0.5) WITHIN GROUP(ORDER BY seconds))::numeric,1)) FROM durations),
  'byMode',(SELECT jsonb_agg(jsonb_build_object('mode',m.mode,'plays',(SELECT count(*) FROM cohort c WHERE c.mode=m.mode),'completed',(SELECT count(result_at) FROM cohort c WHERE c.mode=m.mode)) ORDER BY m.ord)
    FROM unnest(ARRAY['Learn','Guided','Assessment']) WITH ORDINALITY m(mode,ord)),
  'byTraining',COALESCE((SELECT jsonb_agg(jsonb_build_object('trainingId',x.training_id,'plays',x.plays,'completed',x.completed,'passed',x.passed) ORDER BY x.plays DESC,x.training_id) FROM (
    SELECT training_id,count(*) plays,count(result_at) completed,count(*) FILTER(WHERE outcome='Passed') passed FROM cohort GROUP BY training_id ORDER BY count(*) DESC,training_id LIMIT 100) x),'[]'),
  'byOrganization',CASE WHEN p_org IS NULL THEN COALESCE((SELECT jsonb_agg(jsonb_build_object('organizationId',x.organization_id,'plays',x.plays,'uniqueTrainees',x.trainees,'completed',x.completed) ORDER BY x.plays DESC,x.organization_id) FROM (
    SELECT organization_id,count(*) plays,count(DISTINCT trainee_user_id) trainees,count(result_at) completed FROM cohort GROUP BY organization_id ORDER BY count(*) DESC,organization_id LIMIT 100) x),'[]') END)
 FROM totals t
$$;

-- Revenue is money received: Applied payment transactions by receipt time, never quotation totals.
CREATE FUNCTION analytics_revenue(p_from timestamptz,p_to timestamptz,p_as_of timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH applied AS (
  SELECT tx.id,tx.received_amount amount,tx.received_currency currency,tx.received_at,pr.organization_id,COALESCE(q.billing_purpose::text,'Unknown') purpose
  FROM payment_transactions tx JOIN payos_payment_requests pr ON pr.id=tx.payment_request_id LEFT JOIN quotations q ON q.id=pr.quotation_id
  WHERE tx.status='Applied' AND tx.received_at>=p_from AND tx.received_at<p_to AND tx.received_at<=p_as_of)
 SELECT jsonb_build_object(
  'totals',COALESCE((SELECT jsonb_agg(jsonb_build_object('currency',currency,'amount',amount,'transactions',n) ORDER BY currency) FROM (SELECT currency,sum(amount) amount,count(*) n FROM applied GROUP BY currency) x),'[]'),
  'byPurpose',COALESCE((SELECT jsonb_agg(jsonb_build_object('purpose',purpose,'currency',currency,'amount',amount,'transactions',n) ORDER BY purpose,currency) FROM (SELECT purpose,currency,sum(amount) amount,count(*) n FROM applied GROUP BY purpose,currency) x),'[]'),
  'byDay',COALESCE((SELECT jsonb_agg(jsonb_build_object('date',received_day,'currency',currency,'amount',amount,'transactions',n) ORDER BY received_day,currency) FROM (
    SELECT to_char(received_at AT TIME ZONE 'UTC','YYYY-MM-DD') AS received_day,currency,sum(amount) amount,count(*) n FROM applied GROUP BY 1,2) x),'[]'),
  'payingOrganizations',(SELECT count(DISTINCT organization_id) FROM applied))
$$;

CREATE FUNCTION analytics_gate(p_report text,p_actor uuid,p_family uuid,p_from timestamptz,p_to timestamptz,p_active_seconds integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;as_of timestamptz:=transaction_timestamp();body jsonb;
BEGIN
 IF p_report NOT IN('OrganizationTraining','PlatformTraining','PlatformRevenue') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_from IS NULL OR p_to IS NULL OR p_to<=p_from OR p_to-p_from>interval '90 days' OR p_active_seconds NOT BETWEEN 10 AND 3600 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_report='OrganizationTraining' THEN
  IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT * INTO org FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL;
  IF org.id IS NULL THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  body:=analytics_training(org.id,p_from,p_to,as_of,p_active_seconds)-'byOrganization'||jsonb_build_object('aiUsage',analytics_ai_usage(org.id,p_from,p_to,as_of));
 ELSE
  IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  body:=CASE WHEN p_report='PlatformTraining' THEN analytics_training(NULL,p_from,p_to,as_of,p_active_seconds)||jsonb_build_object('aiUsage',analytics_ai_usage(NULL,p_from,p_to,as_of))
   ELSE analytics_revenue(p_from,p_to,as_of) END;
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('asOf',as_of,'from',p_from,'to',p_to)||body);
END $$;

DO $grants$ DECLARE t text;r text;sig text;s record;BEGIN
 FOREACH t IN ARRAY ARRAY['payment_transactions','payos_payment_requests','quotations'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=t AND policyname='analytics_owner_read') THEN
   EXECUTE format('CREATE POLICY analytics_owner_read ON %I FOR SELECT TO fet3d_ifc_upload_owner USING(true)',t);
  END IF;
 END LOOP;
 GRANT SELECT ON payment_transactions,payos_payment_requests,quotations,sessions,session_results TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['analytics_ai_usage(uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone)',
   'analytics_training(uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone,integer)',
   'analytics_revenue(timestamp with time zone,timestamp with time zone,timestamp with time zone)',
   'analytics_gate(text,uuid,uuid,timestamp with time zone,timestamp with time zone,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION analytics_gate(text,uuid,uuid,timestamp with time zone,timestamp with time zone,integer) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM learner_analytics_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
