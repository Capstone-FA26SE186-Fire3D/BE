-- Trusted SDK verification writes a normalized, PII-free inbox. Only this gate may apply it.
CREATE OR REPLACE FUNCTION process_payos_inbox(p_inbox uuid,p_lease uuid) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ev public.payos_webhook_inbox%ROWTYPE; req public.payos_payment_requests%ROWTYPE;
 op public.billing_checkout_operations%ROWTYPE; old public.payment_transactions%ROWTYPE;
 tid uuid; reason text; quote uuid;
BEGIN
 SELECT * INTO ev FROM public.payos_webhook_inbox WHERE id=p_inbox FOR UPDATE;
 IF NOT FOUND OR ev.lease_token IS DISTINCT FROM p_lease OR ev.lease_until<=clock_timestamp()
  OR ev.verified_at IS NULL THEN RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 SELECT quotation_id INTO quote FROM public.billing_checkout_operations WHERE order_code=ev.order_code;
 IF quote IS NULL THEN
  UPDATE public.payos_webhook_inbox SET status='NeedsReconcile',last_error='PAYOS_UNKNOWN_ORDER',lease_token=NULL,lease_until=NULL,
   next_attempt_at=clock_timestamp()+interval '30 minutes' WHERE id=ev.id;RETURN NULL;
 END IF;
 -- Same order as checkout binding: operation -> quotation -> request. No identity dependency for accepted money.
 SELECT * INTO op FROM public.billing_checkout_operations WHERE order_code=ev.order_code FOR UPDATE;
 PERFORM 1 FROM public.quotations WHERE id=quote FOR UPDATE;
 SELECT * INTO req FROM public.payos_payment_requests WHERE id=op.payment_request_id FOR UPDATE;
 IF NOT FOUND THEN
  UPDATE public.payos_webhook_inbox SET status='NeedsReconcile',last_error='PAYOS_AWAITING_BIND',lease_token=NULL,lease_until=NULL,
   next_attempt_at=clock_timestamp()+interval '30 seconds' WHERE id=ev.id;RETURN NULL;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fet3d:payos:reference:'||(ev.payload->>'Reference'),0));
 SELECT * INTO old FROM public.payment_transactions WHERE provider_transaction_id=ev.payload->>'Reference';
 IF FOUND THEN
  IF old.payment_request_id<>req.id OR old.raw_payload IS DISTINCT FROM ev.payload THEN
   RAISE EXCEPTION 'PAYOS_REFERENCE_CONFLICT'; END IF;
  UPDATE public.payos_webhook_inbox SET status=old.status::text,processed_at=clock_timestamp(),last_error=old.rejection_reason,
   lease_token=NULL,lease_until=NULL WHERE id=ev.id;RETURN old.id;
 END IF;
 IF (ev.payload->>'OrderCode')::bigint IS DISTINCT FROM req.order_code
  OR (ev.payload->>'Amount')::numeric IS DISTINCT FROM req.expected_amount
  OR ev.payload->>'Currency' IS DISTINCT FROM req.expected_currency
  OR ev.payload->>'Currency'<>'VND'
  OR ev.payload->>'PaymentLinkId' IS DISTINCT FROM req.payment_link_id THEN reason:='PAYOS_SNAPSHOT_MISMATCH';
 ELSIF EXISTS(SELECT 1 FROM public.payos_payment_requests WHERE quotation_id=quote AND status='Paid') THEN
  reason:='PAYOS_QUOTATION_ALREADY_PAID';
 END IF;
 INSERT INTO public.payment_transactions(payment_request_id,webhook_event_id,provider_transaction_id,received_order_code,received_amount,received_currency,raw_payload)
 VALUES(req.id,ev.event_key,ev.payload->>'Reference',(ev.payload->>'OrderCode')::bigint,(ev.payload->>'Amount')::numeric,ev.payload->>'Currency',ev.payload) RETURNING id INTO tid;
 UPDATE public.payment_transactions SET status='Verified',signature_verified=true,signature_verified_at=ev.verified_at WHERE id=tid;
 IF reason IS NOT NULL THEN
  UPDATE public.payment_transactions SET status='Rejected',rejection_reason=reason,processed_at=clock_timestamp() WHERE id=tid;
 ELSE
  UPDATE public.payment_transactions SET status='Applied',processed_at=clock_timestamp() WHERE id=tid;
  UPDATE public.payos_payment_requests SET status='Paid',paid_transaction_id=tid,paid_at=clock_timestamp() WHERE id=req.id;
  INSERT INTO public.payment_provisioning_records(id,payment_transaction_id,quotation_id,quotation_item_id,organization_id,provisioning_key,status,attempts,created_at,updated_at,next_attempt_at)
  SELECT gen_random_uuid(),tid,quote,item.id,req.organization_id,'service:'||item.id::text||':'||tid::text,'Pending',0,clock_timestamp(),clock_timestamp(),clock_timestamp()
   FROM public.quotation_building_items item JOIN public.quotations q ON q.id=item.quotation_id
   WHERE q.id=quote AND q.billing_purpose='BuildingService';
  UPDATE public.billing_checkout_operations SET status='Completed',lease_token=NULL,lease_until=NULL,last_error=NULL,updated_at=clock_timestamp() WHERE id=op.id;
 END IF;
 UPDATE public.payos_webhook_inbox SET status=CASE WHEN reason IS NULL THEN 'Applied' ELSE 'Rejected' END,
  last_error=reason,processed_at=clock_timestamp(),lease_token=NULL,lease_until=NULL WHERE id=ev.id;
 INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),req.organization_id,'System','Payment','payment_transactions',tid,op.id,
  jsonb_build_object('operation',CASE WHEN reason IS NULL THEN 'PaymentApplied' ELSE 'PaymentRejected' END,'reason',reason),clock_timestamp());
 RETURN tid;
END $$;
GRANT SELECT,UPDATE ON payos_webhook_inbox TO fet3d_payos_ledger_owner;
GRANT SELECT,INSERT ON payment_provisioning_records TO fet3d_payos_ledger_owner;
GRANT SELECT ON quotation_building_items TO fet3d_payos_ledger_owner;
GRANT SELECT ON quotations TO fet3d_payos_ledger_owner;
DO $ownership$
DECLARE had_create boolean:=has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE');
BEGIN
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
 ALTER FUNCTION process_payos_inbox(uuid,uuid) OWNER TO fet3d_payos_ledger_owner;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;END IF;
END $ownership$;
REVOKE ALL ON FUNCTION process_payos_inbox(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION process_payos_inbox(uuid,uuid) TO fet3d_payos_webhook_executor;
-- Legacy primitive cannot skip runtime inbox/payment-link checks.
REVOKE EXECUTE ON FUNCTION apply_verified_payos_webhook(uuid,text,text,bigint,numeric,text,jsonb) FROM fet3d_payos_webhook_executor;

CREATE OR REPLACE FUNCTION validate_payos_paid_request()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.quotations AS quotation
         WHERE quotation.id = NEW.quotation_id
           AND quotation.organization_id = NEW.organization_id
           AND quotation.total_amount = NEW.expected_amount
           AND quotation.currency = NEW.expected_currency
    ) THEN RAISE EXCEPTION 'PayOS request must match quotation organization, amount and currency'; END IF;

    IF TG_OP = 'INSERT' AND NOT EXISTS (
        SELECT 1 FROM public.users AS requester
         WHERE requester.id = NEW.requested_by AND requester.role = 'OrganizationUser'
           AND requester.organization_id = NEW.organization_id
           AND requester.is_active AND requester.deleted_at IS NULL
    ) THEN RAISE EXCEPTION 'checkout requester must be an active OrganizationUser in the same tenant'; END IF;

    IF TG_OP = 'UPDATE' AND (
        OLD.quotation_id IS DISTINCT FROM NEW.quotation_id
        OR OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.requested_by IS DISTINCT FROM NEW.requested_by
        OR OLD.idempotency_key IS DISTINCT FROM NEW.idempotency_key
        OR OLD.order_code IS DISTINCT FROM NEW.order_code
        OR OLD.expected_amount IS DISTINCT FROM NEW.expected_amount
        OR OLD.expected_currency IS DISTINCT FROM NEW.expected_currency
    ) THEN RAISE EXCEPTION 'PayOS request identity and quotation snapshot are immutable'; END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Paid' AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Paid payment truth and provenance are immutable';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND OLD.status NOT IN ('Pending','Cancelled','Expired') THEN
        RAISE EXCEPTION 'Only a Pending, Cancelled or Expired request can become Paid through verified payment';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND CURRENT_USER <> 'fet3d_payos_ledger_owner' THEN
        RAISE EXCEPTION 'Paid payment request must use the trusted PayOS webhook function';
    END IF;
    IF NEW.status = 'Paid' AND NOT EXISTS (
        SELECT 1 FROM public.payment_transactions AS payment
         WHERE payment.id = NEW.paid_transaction_id AND payment.payment_request_id = NEW.id
           AND payment.status = 'Applied' AND payment.signature_verified
           AND payment.received_order_code = NEW.order_code
           AND payment.received_amount = NEW.expected_amount
           AND payment.received_currency = NEW.expected_currency
    ) THEN RAISE EXCEPTION 'Paid requires an Applied verified webhook matching the request snapshot'; END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
