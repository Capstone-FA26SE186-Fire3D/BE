\set ON_ERROR_STOP on
BEGIN;
\ir fixtures.sql
CREATE TEMP TABLE phase2_checks(name TEXT,outcome TEXT);
INSERT INTO service_packages(id,code,name,unit_price,created_by)
VALUES(pg_temp.tid(1),'TEST','Synthetic package',1000,pg_temp.tid(6));
INSERT INTO quotations(id,organization_id,service_package_id,requested_by,issued_by,quotation_number,status,
quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,issued_at,accepted_at)
VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(6),'TEST-Q1','Accepted',
1,1000,1000,0,0,1000,'VND',now()+interval '1 day',now(),now());
DO $$ DECLARE req UUID; tx UUID; duplicate_tx UUID; failed BOOLEAN:=false; BEGIN
 req:=create_pending_payos_payment_request(pg_temp.tid(1),pg_temp.tid(1),10001,'https://example.test/checkout',
 'https://example.test/return','https://example.test/cancel',now()+interval '1 hour');
 IF NOT EXISTS(SELECT 1 FROM payos_payment_requests WHERE id=req AND status='Pending' AND expected_amount=1000)
 THEN RAISE EXCEPTION 'Pending creation failed'; END IF;
 INSERT INTO phase2_checks VALUES('Pending request derived from quotation','PASS');
 BEGIN UPDATE payos_payment_requests SET status='Paid',paid_at=now() WHERE id=req;
 EXCEPTION WHEN OTHERS THEN
   IF SQLERRM NOT ILIKE '%trusted PayOS webhook%' THEN RAISE; END IF; failed:=true;
 END;
 IF NOT failed THEN RAISE EXCEPTION 'Direct Paid update should fail'; END IF;
 INSERT INTO phase2_checks VALUES('Direct Paid update denied','PASS');
 tx:=apply_verified_payos_webhook(req,'synthetic-mismatch','provider-mismatch',10001,999,'VND','{"synthetic":true,"amount":999}');
 IF NOT EXISTS(SELECT 1 FROM payment_transactions WHERE id=tx AND status='Rejected')
 OR NOT EXISTS(SELECT 1 FROM payos_payment_requests WHERE id=req AND status='Pending') THEN RAISE EXCEPTION 'Mismatch handling failed'; END IF;
 INSERT INTO phase2_checks VALUES('Verified mismatched amount rejected','PASS');
 tx:=apply_verified_payos_webhook(req,'synthetic-paid','provider-paid',10001,1000,'VND','{"synthetic":true,"amount":1000}');
 duplicate_tx:=apply_verified_payos_webhook(req,'synthetic-paid','provider-paid',10001,1000,'VND','{"synthetic":true,"amount":1000}');
 IF tx<>duplicate_tx OR NOT EXISTS(SELECT 1 FROM payos_payment_requests WHERE id=req AND status='Paid' AND paid_transaction_id=tx)
 THEN RAISE EXCEPTION 'Paid or webhook retry failed'; END IF;
 INSERT INTO phase2_checks VALUES('Applied webhook marks Paid with provenance','PASS'),('Exact webhook retry idempotent','PASS');
 IF has_table_privilege('fet3d_payos_request_executor','payos_payment_requests','INSERT')
 OR has_table_privilege('fet3d_payos_webhook_executor','payment_transactions','INSERT')
 THEN RAISE EXCEPTION 'Executor has direct DML'; END IF;
 INSERT INTO phase2_checks VALUES('PayOS executors have no table INSERT','PASS');
END $$;
SELECT name,outcome FROM phase2_checks;
SELECT count(*) AS passed_tests FROM phase2_checks;
ROLLBACK;
