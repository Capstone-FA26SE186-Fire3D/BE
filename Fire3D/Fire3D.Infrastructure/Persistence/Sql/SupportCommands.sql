
-- Preflight preserves legacy content; invalid data must be reviewed, never silently truncated.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM feedback WHERE length(btrim(category)) NOT BETWEEN 1 AND 100 OR length(btrim(message)) NOT BETWEEN 1 AND 10000 OR rating NOT BETWEEN 1 AND 5)
 OR EXISTS(SELECT 1 FROM support_tickets WHERE length(btrim(subject)) NOT BETWEEN 1 AND 255 OR length(btrim(description)) NOT BETWEEN 1 AND 10000)
 OR EXISTS(SELECT 1 FROM support_ticket_messages WHERE length(btrim(message)) NOT BETWEEN 1 AND 10000) THEN RAISE EXCEPTION 'Support content preflight failed; review legacy data before migration';END IF;
END $$;
ALTER TABLE feedback ADD COLUMN revision bigint NOT NULL DEFAULT 1 CHECK(revision>0);
ALTER TABLE support_tickets ADD COLUMN revision bigint NOT NULL DEFAULT 1 CHECK(revision>0);
ALTER TABLE feedback ADD CONSTRAINT feedback_content_valid CHECK(length(btrim(category)) BETWEEN 1 AND 100 AND length(btrim(message)) BETWEEN 1 AND 10000 AND (rating IS NULL OR rating BETWEEN 1 AND 5));
ALTER TABLE support_tickets ADD CONSTRAINT support_content_valid CHECK(length(btrim(subject)) BETWEEN 1 AND 255 AND length(btrim(description)) BETWEEN 1 AND 10000);
CREATE TABLE support_command_receipts(actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),result jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(actor_id,operation,idempotency_key));
CREATE INDEX support_tickets_owner_page ON support_tickets(created_by,created_at DESC,id);
CREATE INDEX feedback_owner_page ON feedback(submitted_by,created_at DESC,id);
CREATE INDEX support_messages_page ON support_ticket_messages(ticket_id,created_at,id);
ALTER TABLE support_command_receipts ENABLE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE ON feedback,support_tickets TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT ON support_ticket_messages,support_command_receipts TO fet3d_ifc_upload_owner;
GRANT SELECT ON sessions TO fet3d_ifc_upload_owner;
DO $$ DECLARE t text;BEGIN FOREACH t IN ARRAY ARRAY['feedback','support_tickets','support_ticket_messages','support_command_receipts','sessions'] LOOP
 EXECUTE format('CREATE POLICY support_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
END LOOP;END $$;
CREATE FUNCTION append_only_support_message() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN RAISE EXCEPTION 'Support message history is append-only';END $$;
CREATE TRIGGER append_only_support_message BEFORE UPDATE OR DELETE ON support_ticket_messages FOR EACH ROW EXECUTE FUNCTION append_only_support_message();
CREATE FUNCTION support_feedback_representation(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',id,'category',category,'message',message,'rating',rating,'status',status,'reviewedBy',reviewed_by,'reviewedAt',reviewed_at,'revision',revision,'createdAt',created_at,'updatedAt',updated_at) FROM feedback WHERE id=p_id
$$;
CREATE FUNCTION support_ticket_representation(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',id,'ticketNumber',ticket_number,'subject',subject,'description',description,'status',status,'priority',priority,'assignedTo',assigned_to,'resolvedAt',resolved_at,'revision',revision,'createdAt',created_at,'updatedAt',updated_at) FROM support_tickets WHERE id=p_id
$$;
CREATE FUNCTION support_command_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;t support_tickets;f feedback;org uuid;session_org uuid;receipt support_command_receipts;h text;result jsonb;new_id uuid;page_no int;page_size int;is_admin boolean;mutation boolean;is_feedback boolean;messages jsonb;
BEGIN
 IF p_action NOT IN('CreateFeedback','CreateTicket','Message','AdminMessage','FeedbackStatus','TicketStatus','ListFeedback','ListTickets','GetTicket','AdminListFeedback','AdminListTickets','AdminGetTicket') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 is_admin:=p_action IN('AdminMessage','FeedbackStatus','TicketStatus','AdminListFeedback','AdminListTickets','AdminGetTicket');
 mutation:=p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage','FeedbackStatus','TicketStatus');
 is_feedback:=p_action IN('CreateFeedback','FeedbackStatus','ListFeedback','AdminListFeedback');
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF (is_admin AND actor.role<>'PlatformAdmin') OR (NOT is_admin AND actor.role NOT IN('Trainee','OrganizationUser')) THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF actor.organization_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 org:=actor.organization_id;
 IF p_resource IS NOT NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:support:'||(CASE WHEN is_feedback THEN 'feedback' ELSE 'ticket' END)||':'||p_resource,0));
  IF is_feedback THEN
   SELECT * INTO f FROM feedback WHERE id=p_resource AND (is_admin OR submitted_by=p_actor);
   IF f.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;org:=f.organization_id;
  ELSE
   SELECT * INTO t FROM support_tickets WHERE id=p_resource AND (is_admin OR created_by=p_actor);
   IF t.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;org:=t.organization_id;
  END IF;
  IF org IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=org AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  SELECT * INTO receipt FROM support_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;RETURN jsonb_build_object('code','OK','result',receipt.result);END IF;
 END IF;
 IF p_action IN('FeedbackStatus','TicketStatus') THEN
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  IF p_expected<>(CASE WHEN is_feedback THEN f.revision ELSE t.revision END) THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
 END IF;
 IF p_action IN('CreateFeedback','CreateTicket') THEN
  IF p_input->>'sessionId' IS NOT NULL THEN
   SELECT organization_id INTO session_org FROM sessions WHERE id=(p_input->>'sessionId')::uuid AND trainee_user_id=p_actor AND (actor.organization_id IS NULL OR organization_id=actor.organization_id);
   IF session_org IS NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=session_org AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','SUPPORT_REFERENCE_NOT_FOUND','status',404);END IF;org:=session_org;
  END IF;
  new_id:=gen_random_uuid();
  IF p_action='CreateFeedback' THEN
   IF length(btrim(p_input->>'category')) NOT BETWEEN 1 AND 100 OR length(btrim(p_input->>'message')) NOT BETWEEN 1 AND 10000 OR (p_input->>'category' IS NULL) OR (p_input->>'message' IS NULL) OR (p_input->>'rating')::int NOT BETWEEN 1 AND 5 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
   INSERT INTO feedback(id,submitted_by,organization_id,session_id,category,message,rating,status,created_at,updated_at)
    VALUES(new_id,p_actor,org,(p_input->>'sessionId')::uuid,btrim(p_input->>'category'),btrim(p_input->>'message'),(p_input->>'rating')::int,'Submitted',now(),now());result:=support_feedback_representation(new_id);
  ELSE
   IF length(btrim(p_input->>'subject')) NOT BETWEEN 1 AND 255 OR length(btrim(p_input->>'description')) NOT BETWEEN 1 AND 10000 OR p_input->>'subject' IS NULL OR p_input->>'description' IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
   IF p_input->>'feedbackId' IS NOT NULL THEN
    SELECT * INTO f FROM feedback WHERE id=(p_input->>'feedbackId')::uuid AND submitted_by=p_actor;
    IF f.id IS NULL OR (org IS NOT NULL AND f.organization_id IS DISTINCT FROM org) THEN RETURN jsonb_build_object('code','SUPPORT_REFERENCE_NOT_FOUND','status',404);END IF;
    org:=f.organization_id;
    IF org IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=org AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','SUPPORT_REFERENCE_NOT_FOUND','status',404);END IF;
   END IF;
   INSERT INTO support_tickets(id,ticket_number,created_by,organization_id,session_id,feedback_id,subject,description,status,priority,created_at,updated_at)
    VALUES(new_id,'SUP-'||replace(new_id::text,'-',''),p_actor,org,(p_input->>'sessionId')::uuid,(p_input->>'feedbackId')::uuid,btrim(p_input->>'subject'),btrim(p_input->>'description'),'Open','Normal',now(),now());result:=support_ticket_representation(new_id);
  END IF;
 ELSIF p_action IN('Message','AdminMessage') THEN
  IF t.status='Closed' THEN RETURN jsonb_build_object('code','TICKET_CLOSED','status',409);END IF;
  IF p_input->>'message' IS NULL OR length(btrim(p_input->>'message')) NOT BETWEEN 1 AND 10000 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  new_id:=gen_random_uuid();INSERT INTO support_ticket_messages VALUES(new_id,t.id,p_actor,btrim(p_input->>'message'),now());
  UPDATE support_tickets SET revision=revision+1,updated_at=now() WHERE id=t.id;
  result:=jsonb_build_object('id',new_id,'authorId',p_actor,'message',btrim(p_input->>'message'),'createdAt',now());
 ELSIF p_action='FeedbackStatus' THEN
  IF p_input->>'status' IS NULL OR p_input->>'status' NOT IN('Submitted','Reviewed','Closed') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF f.status::text<>p_input->>'status' AND NOT ((f.status='Submitted' AND p_input->>'status'='Reviewed') OR(f.status='Reviewed' AND p_input->>'status'='Closed')) THEN RETURN jsonb_build_object('code','INVALID_STATUS_TRANSITION','status',409);END IF;
  IF f.status::text=p_input->>'status' THEN RETURN jsonb_build_object('code','OK','result',support_feedback_representation(f.id));END IF;
  UPDATE feedback SET status=(p_input->>'status')::feedback_status_enum,reviewed_by=p_actor,reviewed_at=now(),revision=revision+1,updated_at=now() WHERE id=f.id;new_id:=f.id;result:=support_feedback_representation(f.id);
 ELSIF p_action='TicketStatus' THEN
  IF p_input->>'status' IS NULL OR p_input->>'status' NOT IN('Open','InProgress','Resolved','Closed') OR p_input->>'priority' IS NULL OR p_input->>'priority' NOT IN('Low','Normal','High','Urgent') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF t.status::text<>p_input->>'status' AND NOT((t.status='Open' AND p_input->>'status'='InProgress') OR(t.status='InProgress' AND p_input->>'status'='Resolved') OR(t.status='Resolved' AND p_input->>'status' IN('Closed','Open')) OR(t.status='Closed' AND p_input->>'status'='Open')) THEN RETURN jsonb_build_object('code','INVALID_STATUS_TRANSITION','status',409);END IF;
  IF p_input->>'assignedTo' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM users WHERE id=(p_input->>'assignedTo')::uuid AND role='PlatformAdmin' AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('assignedTo',jsonb_build_array('Select an active PlatformAdmin.')));END IF;
  UPDATE support_tickets SET status=(p_input->>'status')::support_ticket_status_enum,priority=(p_input->>'priority')::support_priority_enum,assigned_to=(p_input->>'assignedTo')::uuid,resolved_at=CASE WHEN p_input->>'status' IN('Resolved','Closed') THEN COALESCE(resolved_at,now()) ELSE NULL END,revision=revision+1,updated_at=now() WHERE id=t.id;new_id:=t.id;result:=support_ticket_representation(t.id);
 ELSE
  page_no:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF page_no NOT BETWEEN 1 AND 100000 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF p_action IN('GetTicket','AdminGetTicket') THEN
   WITH all_rows AS MATERIALIZED(SELECT * FROM support_ticket_messages WHERE ticket_id=t.id),
   rows AS(SELECT * FROM all_rows ORDER BY created_at,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',id,'authorId',author_id,'message',message,'createdAt',created_at) ORDER BY created_at,id) FROM rows),'[]'::jsonb),'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO messages;
   result:=support_ticket_representation(t.id)||jsonb_build_object('messages',messages);
  ELSIF is_feedback THEN
   WITH all_rows AS MATERIALIZED(SELECT id,created_at FROM feedback WHERE (is_admin OR submitted_by=p_actor) AND (p_input->>'status' IS NULL OR status::text=p_input->>'status') AND (NOT is_admin OR p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid)),
   rows AS(SELECT * FROM all_rows ORDER BY created_at DESC,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(support_feedback_representation(id) ORDER BY created_at DESC,id) FROM rows),'[]'::jsonb),'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO result;
  ELSE
   WITH all_rows AS MATERIALIZED(SELECT id,created_at FROM support_tickets WHERE (is_admin OR created_by=p_actor) AND (p_input->>'status' IS NULL OR status::text=p_input->>'status') AND (p_input->>'priority' IS NULL OR priority::text=p_input->>'priority') AND (p_input->>'assignedTo' IS NULL OR assigned_to=(p_input->>'assignedTo')::uuid) AND (NOT is_admin OR p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid)),
   rows AS(SELECT * FROM all_rows ORDER BY created_at DESC,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(support_ticket_representation(id) ORDER BY created_at DESC,id) FROM rows),'[]'::jsonb),'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO result;
  END IF;
 END IF;
 IF mutation THEN
  IF p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage') THEN INSERT INTO support_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);END IF;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,org,'User','Support',CASE WHEN is_feedback THEN 'Feedback' ELSE 'SupportTicket' END,CASE WHEN p_action IN('Message','AdminMessage') THEN t.id ELSE new_id END,gen_random_uuid(),jsonb_build_object('operation',p_action,'revision',result->'revision','status',result->'status'),now());
 END IF;
 RETURN jsonb_build_object('code','OK','result',result);
END $$;
DO $$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;sig text;r text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user);END IF;GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['support_command_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','support_feedback_representation(uuid)','support_ticket_representation(uuid)'] LOOP EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION support_command_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON feedback,support_tickets,support_ticket_messages,support_command_receipts FROM %I',r);END IF;END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF changed THEN IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
