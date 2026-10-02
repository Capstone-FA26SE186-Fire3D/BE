ALTER TABLE billing_checkout_operations ADD COLUMN IF NOT EXISTS session_family_id uuid;
CREATE OR REPLACE FUNCTION bind_payos_checkout(p_checkout uuid,p_lease uuid) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE op public.billing_checkout_operations%ROWTYPE; rid uuid;
BEGIN
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO op FROM public.billing_checkout_operations WHERE id=p_checkout FOR UPDATE;
 IF NOT FOUND OR op.lease_token IS DISTINCT FROM p_lease OR op.lease_until<=clock_timestamp() THEN
  RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 IF op.payment_request_id IS NOT NULL THEN RETURN op.payment_request_id; END IF;
 IF op.provider_result IS NULL OR op.payment_link_id IS NULL OR op.provider_input='{}'::jsonb THEN
  RAISE EXCEPTION 'PAYOS_PROVIDER_RESULT_MISSING'; END IF;
 IF NOT EXISTS(SELECT 1 FROM public.auth_refresh_tokens WHERE user_id=op.actor_id
  AND family_id=op.session_family_id AND revoked_at IS NULL AND consumed_at IS NULL AND expires_at>clock_timestamp()) THEN
  RAISE EXCEPTION 'PAYOS_SESSION_REVOKED'; END IF;
 IF (op.provider_result->>'OrderCode')::bigint IS DISTINCT FROM op.order_code
  OR (op.provider_result->>'Amount')::numeric IS DISTINCT FROM (op.provider_input->>'Amount')::numeric
  OR op.provider_result->>'Currency'<>'VND'
  OR op.provider_result->>'PaymentLinkId' IS DISTINCT FROM op.payment_link_id THEN
  RAISE EXCEPTION 'PAYOS_PROVIDER_RESULT_MISMATCH'; END IF;
 rid:=public.create_pending_payos_payment_request(op.quotation_id,op.actor_id,'checkout:'||op.id::text,op.order_code,
  op.provider_result->>'CheckoutUrl',op.provider_input->>'ReturnUrl',op.provider_input->>'CancelUrl',op.expires_at);
 UPDATE public.payos_payment_requests SET payment_link_id=op.payment_link_id WHERE id=rid;
 UPDATE public.billing_checkout_operations SET payment_request_id=rid,status='Ready',last_error=NULL,attempts=0,
  lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp(),next_attempt_at=clock_timestamp()+interval '30 seconds' WHERE id=op.id;
 INSERT INTO public.audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 SELECT gen_random_uuid(),op.actor_id,q.organization_id,'User','Payment','payos_payment_requests',rid,op.id,
  jsonb_build_object('operation','CheckoutCreated','orderCode',op.order_code),clock_timestamp()
 FROM public.quotations q WHERE q.id=op.quotation_id;
 RETURN rid;
END $$;
CREATE OR REPLACE FUNCTION mark_payos_navigation_state(p_checkout uuid,p_lease uuid,p_status text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE op public.billing_checkout_operations%ROWTYPE;
BEGIN
 SELECT * INTO op FROM public.billing_checkout_operations WHERE id=p_checkout FOR UPDATE;
 IF NOT FOUND OR op.lease_token IS DISTINCT FROM p_lease OR op.lease_until<=clock_timestamp()
  OR p_status NOT IN('Cancelled','Expired') OR op.provider_result->>'Status' IS DISTINCT FROM p_status
  OR coalesce((op.provider_result->>'AmountPaid')::bigint,0)<>0 THEN RAISE EXCEPTION 'PAYOS_INVALID_CANCEL'; END IF;
 IF op.payment_request_id IS NOT NULL THEN
  PERFORM 1 FROM public.payos_payment_requests WHERE id=op.payment_request_id FOR UPDATE;
  IF EXISTS(SELECT 1 FROM public.payos_payment_requests WHERE id=op.payment_request_id AND status='Paid') THEN
   UPDATE public.billing_checkout_operations SET status='Completed',lease_token=NULL,lease_until=NULL WHERE id=op.id;RETURN;END IF;
  UPDATE public.payos_payment_requests SET status=p_status::public.payment_request_status_enum,updated_at=clock_timestamp()
   WHERE id=op.payment_request_id;
 END IF;
 UPDATE public.billing_checkout_operations SET status=p_status,lease_token=NULL,lease_until=NULL,
  last_error=NULL,updated_at=clock_timestamp() WHERE id=op.id;
 INSERT INTO public.audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 SELECT gen_random_uuid(),NULL,q.organization_id,'System','Payment','billing_checkout_operations',op.id,op.id,
  jsonb_build_object('operation',p_status),clock_timestamp() FROM public.quotations q WHERE q.id=op.quotation_id;
END $$;
GRANT SELECT,UPDATE ON billing_checkout_operations TO fet3d_payos_ledger_owner;
GRANT SELECT(user_id,family_id,revoked_at,consumed_at,expires_at) ON auth_refresh_tokens TO fet3d_payos_ledger_owner;
GRANT INSERT ON audit_logs TO fet3d_payos_ledger_owner;
DO $ownership$
DECLARE had_create boolean:=has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE');
BEGIN
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
 ALTER FUNCTION bind_payos_checkout(uuid,uuid) OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION mark_payos_navigation_state(uuid,uuid,text) OWNER TO fet3d_payos_ledger_owner;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;END IF;
END $ownership$;
REVOKE ALL ON FUNCTION bind_payos_checkout(uuid,uuid),mark_payos_navigation_state(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION bind_payos_checkout(uuid,uuid),mark_payos_navigation_state(uuid,uuid,text) TO fet3d_payos_request_executor;

