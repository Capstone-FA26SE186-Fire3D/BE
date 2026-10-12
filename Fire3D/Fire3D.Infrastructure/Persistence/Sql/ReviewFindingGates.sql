-- Forward fixes from the 12/10/2026 review. Migrations 20261012090000/100000/140000 had their SQL edited after they were
-- merged, so a database that applied the earlier text keeps the old gates. This re-creates the current definitions of
-- catalog_search_pattern/catalog_list_gate (knowledge-source id filter, every visibility by default for the admin registry)
-- and support_command_gate (Admin writes on inactive/deleted organization resources are 404). authoring_readiness_gate now
-- reports RUNTIME_ARTIFACT_REQUIRED before a confirmation exists, since confirm-for-training itself requires the artifact.
-- audit_logs gets a target index for the draft list's last-editor lookup. Owners and grants are unchanged by OR REPLACE.
CREATE TEMP TABLE review_finding_gate_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO review_finding_gate_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION catalog_search_pattern(p_text text) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN NULLIF(btrim(p_text),'') IS NULL THEN NULL ELSE '%'||replace(replace(replace(btrim(p_text),'\','\\'),'%','\%'),'_','\_')||'%' END
$$;

CREATE OR REPLACE FUNCTION catalog_list_gate(p_action text,p_actor uuid,p_family uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;page integer;size integer;pattern text;result jsonb;total integer;
BEGIN
 IF p_action NOT IN('KnowledgeSources','Situations') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 page:=COALESCE((p_input->>'page')::int,1);size:=COALESCE((p_input->>'pageSize')::int,20);
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 pattern:=catalog_search_pattern(p_input->>'search');
 IF p_action='KnowledgeSources' THEN
  WITH f AS MATERIALIZED(SELECT k.* FROM knowledge_sources k WHERE (p_input->>'visibility' IS NULL OR k.visibility=p_input->>'visibility')
    AND (p_input->>'id' IS NULL OR k.id=(p_input->>'id')::uuid)
    AND (p_input->>'status' IS NULL OR k.approval_status=p_input->>'status')
    AND (pattern IS NULL OR k.title ILIKE pattern ESCAPE '\' OR k.version_label ILIKE pattern ESCAPE '\' OR COALESCE(k.jurisdiction,'') ILIKE pattern ESCAPE '\'))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(x.item ORDER BY x.created_at DESC,x.id) FROM (
    SELECT f.id,f.created_at,jsonb_build_object('id',f.id,'visibility',f.visibility,'organizationId',f.organization_id,'title',f.title,'sourceUri',f.source_uri,
     'versionLabel',f.version_label,'sourceHash',f.source_hash,'jurisdiction',f.jurisdiction,'effectiveFrom',f.effective_from,'effectiveUntil',f.effective_until,
     'approvalStatus',f.approval_status,'approvedAt',f.approved_at,'createdAt',f.created_at,'revision',f.revision,
     'canUseInDraft',f.visibility='Common' AND f.approval_status IN('Draft','Approved'),
     'canPublishReference',f.visibility='Common' AND f.approval_status='Approved' AND (f.effective_from IS NULL OR f.effective_from<=clock_timestamp())
      AND (f.effective_until IS NULL OR f.effective_until>clock_timestamp())) item
    FROM f ORDER BY f.created_at DESC,f.id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 ELSE
  WITH f AS MATERIALIZED(SELECT s.* FROM learn_situations s WHERE (p_input->>'isActive' IS NULL OR s.is_active=(p_input->>'isActive')::boolean)
    AND (pattern IS NULL OR s.slug ILIKE pattern ESCAPE '\' OR s.name ILIKE pattern ESCAPE '\'))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(x.item ORDER BY x.name,x.id) FROM (
    SELECT f.id,f.name,jsonb_build_object('id',f.id,'slug',f.slug,'name',f.name,'description',f.description,'isActive',f.is_active,'createdAt',f.created_at,'updatedAt',f.updated_at) item
    FROM f ORDER BY f.name,f.id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
END $$;

CREATE OR REPLACE FUNCTION support_command_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;t support_tickets;f feedback;org uuid;session_org uuid;receipt support_command_receipts;h text;result jsonb;new_id uuid;page_no int;page_size int;is_admin boolean;mutation boolean;is_feedback boolean;messages jsonb;audit_old jsonb;audit_new jsonb;
BEGIN
 IF p_action NOT IN('CreateFeedback','CreateTicket','Message','AdminMessage','FeedbackStatus','TicketStatus','ListFeedback','ListTickets','GetTicket','AdminListFeedback','AdminListTickets','AdminGetTicket','AdminGetFeedback') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 is_admin:=p_action IN('AdminMessage','FeedbackStatus','TicketStatus','AdminListFeedback','AdminListTickets','AdminGetTicket','AdminGetFeedback');
 mutation:=p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage','FeedbackStatus','TicketStatus');
 is_feedback:=p_action IN('CreateFeedback','FeedbackStatus','ListFeedback','AdminListFeedback','AdminGetFeedback');
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF (is_admin AND actor.role<>'PlatformAdmin') OR (NOT is_admin AND actor.role NOT IN('Trainee','OrganizationUser')) THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF actor.organization_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 org:=actor.organization_id;
 IF p_action='AdminGetFeedback' AND p_resource IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_resource IS NOT NULL THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:support:'||(CASE WHEN is_feedback THEN 'feedback' ELSE 'ticket' END)||':'||p_resource,0));
  IF is_feedback THEN
   SELECT * INTO f FROM feedback WHERE id=p_resource AND (is_admin OR submitted_by=p_actor);
   IF f.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;org:=f.organization_id;
  ELSE
   SELECT * INTO t FROM support_tickets WHERE id=p_resource AND (is_admin OR created_by=p_actor);
   IF t.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;org:=t.organization_id;
  END IF;
  -- Admin reads keep resources of inactive/deleted organizations reachable; user-side access still requires a live organization.
  IF (mutation OR NOT is_admin) AND org IS NOT NULL AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=org AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  SELECT * INTO receipt FROM support_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   -- Replay: authorization above already ran for this caller; labels are recomputed for this caller, never taken from the receipt.
   result:=receipt.result-'createdBy'-'organization'-'author';
   IF p_action IN('Message','AdminMessage') THEN result:=result||jsonb_build_object('author',support_person((result->>'authorId')::uuid,is_admin));
   ELSE result:=result||COALESCE(support_resource_labels(p_action='CreateFeedback',(result->>'id')::uuid,is_admin),'{}'::jsonb);END IF;
   RETURN jsonb_build_object('code','OK','result',result);
  END IF;
 END IF;
 IF p_action IN('FeedbackStatus','TicketStatus') THEN
  IF jsonb_typeof(p_input->'reason')='string' AND (length(p_input->>'reason') NOT BETWEEN 1 AND 1000 OR btrim(p_input->>'reason')='') OR (p_input ? 'reason' AND jsonb_typeof(p_input->'reason') NOT IN('string','null')) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('Provide 1-1000 characters that are not only whitespace.')));END IF;
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
 ELSIF p_action='AdminGetFeedback' THEN
  result:=support_feedback_representation(f.id);
 ELSIF p_action='FeedbackStatus' THEN
  IF p_input->>'status' IS NULL OR p_input->>'status' NOT IN('Submitted','Reviewed','Closed') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF f.status::text<>p_input->>'status' AND NOT ((f.status='Submitted' AND p_input->>'status'='Reviewed') OR(f.status='Reviewed' AND p_input->>'status'='Closed')) THEN RETURN jsonb_build_object('code','INVALID_STATUS_TRANSITION','status',409);END IF;
  IF f.status::text=p_input->>'status' THEN RETURN jsonb_build_object('code','OK','result',support_feedback_representation(f.id)||support_resource_labels(true,f.id,true));END IF;
  audit_old:=jsonb_build_object('status',f.status,'revision',f.revision,'reviewedBy',f.reviewed_by,'reviewedAt',f.reviewed_at);
  UPDATE feedback SET status=(p_input->>'status')::feedback_status_enum,reviewed_by=p_actor,reviewed_at=now(),revision=revision+1,updated_at=now() WHERE id=f.id RETURNING * INTO f;new_id:=f.id;result:=support_feedback_representation(f.id);
  audit_new:=jsonb_build_object('status',f.status,'revision',f.revision,'reviewedBy',f.reviewed_by,'reviewedAt',f.reviewed_at);
 ELSIF p_action='TicketStatus' THEN
  IF p_input->>'status' IS NULL OR p_input->>'status' NOT IN('Open','InProgress','Resolved','Closed') OR p_input->>'priority' IS NULL OR p_input->>'priority' NOT IN('Low','Normal','High','Urgent') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF t.status::text<>p_input->>'status' AND NOT((t.status='Open' AND p_input->>'status'='InProgress') OR(t.status='InProgress' AND p_input->>'status'='Resolved') OR(t.status='Resolved' AND p_input->>'status' IN('Closed','Open')) OR(t.status='Closed' AND p_input->>'status'='Open')) THEN RETURN jsonb_build_object('code','INVALID_STATUS_TRANSITION','status',409);END IF;
  IF p_input->>'assignedTo' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM users WHERE id=(p_input->>'assignedTo')::uuid AND role='PlatformAdmin' AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('assignedTo',jsonb_build_array('Select an active PlatformAdmin.')));END IF;
  audit_old:=jsonb_build_object('status',t.status,'priority',t.priority,'assignedTo',t.assigned_to,'revision',t.revision,'resolvedAt',t.resolved_at);
  UPDATE support_tickets SET status=(p_input->>'status')::support_ticket_status_enum,priority=(p_input->>'priority')::support_priority_enum,assigned_to=(p_input->>'assignedTo')::uuid,resolved_at=CASE WHEN p_input->>'status' IN('Resolved','Closed') THEN COALESCE(resolved_at,now()) ELSE NULL END,revision=revision+1,updated_at=now() WHERE id=t.id RETURNING * INTO t;new_id:=t.id;result:=support_ticket_representation(t.id);
  audit_new:=jsonb_build_object('status',t.status,'priority',t.priority,'assignedTo',t.assigned_to,'revision',t.revision,'resolvedAt',t.resolved_at);
 ELSE
  page_no:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF page_no NOT BETWEEN 1 AND 100000 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF p_action IN('GetTicket','AdminGetTicket') THEN
   -- Message authors are joined once per page; email only on the Admin route.
   WITH all_rows AS MATERIALIZED(SELECT * FROM support_ticket_messages WHERE ticket_id=t.id),
   rows AS(SELECT * FROM all_rows ORDER BY created_at,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',r.id,'authorId',r.author_id,'author',support_person_label(r.author_id,u.id IS NOT NULL,u.full_name,u.username,u.email,is_admin),'message',r.message,'createdAt',r.created_at) ORDER BY r.created_at,r.id)
     FROM rows r LEFT JOIN users u ON u.id=r.author_id AND u.deleted_at IS NULL),'[]'::jsonb),'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO messages;
   result:=support_ticket_representation(t.id)||jsonb_build_object('messages',messages);
  ELSIF is_feedback THEN
   WITH all_rows AS MATERIALIZED(SELECT id,created_at FROM feedback WHERE (is_admin OR submitted_by=p_actor) AND (p_input->>'status' IS NULL OR status::text=p_input->>'status') AND (NOT is_admin OR p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid)),
   rows AS(SELECT * FROM all_rows ORDER BY created_at DESC,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(support_feedback_representation(r.id)||jsonb_build_object(
      'createdBy',support_person_label(x.submitted_by,u.id IS NOT NULL,u.full_name,u.username,u.email,is_admin),'organization',support_org_label(x.organization_id,o.id IS NOT NULL,o.name)) ORDER BY r.created_at DESC,r.id)
     FROM rows r JOIN feedback x ON x.id=r.id LEFT JOIN users u ON u.id=x.submitted_by AND u.deleted_at IS NULL LEFT JOIN organizations o ON o.id=x.organization_id AND o.deleted_at IS NULL),'[]'::jsonb),
    'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO result;
  ELSE
   WITH all_rows AS MATERIALIZED(SELECT id,created_at FROM support_tickets WHERE (is_admin OR created_by=p_actor) AND (p_input->>'status' IS NULL OR status::text=p_input->>'status') AND (p_input->>'priority' IS NULL OR priority::text=p_input->>'priority') AND (p_input->>'assignedTo' IS NULL OR assigned_to=(p_input->>'assignedTo')::uuid) AND (NOT is_admin OR p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid)),
   rows AS(SELECT * FROM all_rows ORDER BY created_at DESC,id LIMIT page_size OFFSET (page_no-1)*page_size)
   SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(support_ticket_representation(r.id)||jsonb_build_object(
      'createdBy',support_person_label(x.created_by,u.id IS NOT NULL,u.full_name,u.username,u.email,is_admin),'organization',support_org_label(x.organization_id,o.id IS NOT NULL,o.name)) ORDER BY r.created_at DESC,r.id)
     FROM rows r JOIN support_tickets x ON x.id=r.id LEFT JOIN users u ON u.id=x.created_by AND u.deleted_at IS NULL LEFT JOIN organizations o ON o.id=x.organization_id AND o.deleted_at IS NULL),'[]'::jsonb),
    'total',(SELECT count(*) FROM all_rows),'page',page_no,'pageSize',page_size) INTO result;
  END IF;
 END IF;
 IF mutation THEN
  -- Receipts store the unlabelled base representation.
  IF p_action IN('CreateFeedback','CreateTicket','Message','AdminMessage') THEN INSERT INTO support_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);END IF;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at) VALUES(gen_random_uuid(),p_actor,org,'User','Support',CASE WHEN is_feedback THEN 'Feedback' ELSE 'SupportTicket' END,CASE WHEN p_action IN('Message','AdminMessage') THEN t.id ELSE new_id END,gen_random_uuid(),
   audit_old,CASE WHEN audit_new IS NOT NULL THEN audit_new||jsonb_build_object('supportAuditContract',1,'operation',p_action,'reason',CASE WHEN jsonb_typeof(p_input->'reason')='string' THEN btrim(p_input->>'reason') END)
    ELSE jsonb_build_object('operation',p_action,'revision',result->'revision','status',result->'status') END,now());
 END IF;
 IF p_action IN('Message','AdminMessage') THEN result:=result||jsonb_build_object('author',support_person(p_actor,is_admin));
 ELSIF p_action IN('CreateFeedback','FeedbackStatus','AdminGetFeedback') THEN result:=result||support_resource_labels(true,(result->>'id')::uuid,is_admin);
 ELSIF p_action IN('CreateTicket','TicketStatus','GetTicket','AdminGetTicket') THEN result:=result||support_resource_labels(false,(result->>'id')::uuid,is_admin);
 END IF;
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

CREATE OR REPLACE FUNCTION authoring_readiness_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;v scenario_versions;rv revisions;run validation_runs;job processing_jobs;
 blockers bigint;confirmation uuid;ready boolean;artifact_ready boolean;missing text[]:='{}';
BEGIN
 IF p_action NOT IN('Version','Revision') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL
  AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role NOT IN('PlatformAdmin','OrganizationUser') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF actor.role='OrganizationUser' AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL)
  THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;

 IF p_action='Version' THEN
  SELECT * INTO v FROM scenario_versions WHERE id=p_resource;
  SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSE
  SELECT * INTO rv FROM revisions WHERE id=p_resource;
  SELECT * INTO b FROM buildings WHERE id=rv.building_id;
 END IF;
 -- Same scope as the review reads: another tenant, or an archived Building for a tenant user, is not found.
 IF b.id IS NULL OR (actor.role='OrganizationUser' AND (b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL))
  THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;

 IF p_action='Version' THEN
  SELECT j.* INTO job FROM processing_jobs j WHERE j.scenario_version_id=v.id AND j.kind='ReleasePackage' ORDER BY j.created_at DESC,j.id DESC LIMIT 1;
  SELECT vr.* INTO run FROM validation_runs vr JOIN processing_jobs j ON j.id=vr.job_id AND j.current_attempt_id=vr.processing_attempt_id
   JOIN processing_job_attempts a ON a.id=vr.processing_attempt_id AND a.processing_job_id=j.id AND a.status='Succeeded'
   WHERE vr.scenario_version_id=v.id AND vr.revision_id=v.revision_id AND vr.scenario_hash=v.scenario_hash AND j.kind='ReleasePackage' AND j.status='Succeeded'
   ORDER BY vr.created_at DESC,vr.id DESC LIMIT 1;
  SELECT count(*) INTO blockers FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical');
  SELECT id INTO confirmation FROM revision_reviews WHERE scenario_version_id=v.id AND revision_id=v.revision_id AND validation_run_id=run.id
   AND annotation_set_id IS NOT DISTINCT FROM run.annotation_set_id AND action='ConfirmForTraining' ORDER BY reviewed_at DESC,id DESC LIMIT 1;
  artifact_ready:=COALESCE(EXISTS(SELECT 1 FROM revision_artifacts WHERE id=run.candidate_artifact_id AND job_id=run.job_id AND attempt_id=run.processing_attempt_id
    AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready),false);
  ready:=COALESCE(run.outcome='Passed' AND blockers=0 AND confirmation IS NOT NULL AND artifact_ready,false);
  IF run.id IS NULL THEN missing:=array_append(missing,'PACKAGE_VALIDATION_REQUIRED');
  ELSE
   IF run.outcome<>'Passed' THEN missing:=array_append(missing,'QA_NOT_PASSED');END IF;
   IF blockers>0 THEN missing:=array_append(missing,'QA_BLOCKERS_PRESENT');END IF;
   IF NOT artifact_ready THEN missing:=array_append(missing,'RUNTIME_ARTIFACT_REQUIRED');END IF;
   IF confirmation IS NULL THEN missing:=array_append(missing,'CONFIRMATION_REQUIRED');END IF;
  END IF;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('scenarioVersionId',v.id,'revisionId',v.revision_id,'scenarioHash',v.scenario_hash,
   'reviewStatus',COALESCE((SELECT status FROM scenario_content_reviews WHERE scenario_version_id=v.id),'NotSubmitted'),
   'packageJob',CASE WHEN job.id IS NULL THEN NULL ELSE jsonb_build_object('jobId',job.id,'status',job.status,'currentAttemptId',job.current_attempt_id) END,
   'validationRunId',run.id,'outcome',run.outcome,'blockerCount',COALESCE(blockers,0),
   'blockers',COALESCE((SELECT jsonb_agg(jsonb_build_object('issueCode',i.code,'severity',i.severity,'message',i.message) ORDER BY i.created_at,i.id)
     FROM (SELECT * FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical') ORDER BY created_at,id LIMIT 50) i),'[]'),
   'confirmationReviewId',confirmation,'candidateArtifactId',run.candidate_artifact_id,'technicalReady',ready,'missing',to_jsonb(missing)));
 END IF;

 SELECT j.* INTO job FROM processing_jobs j WHERE j.revision_id=rv.id AND j.kind='Geometry' ORDER BY j.created_at DESC,j.id DESC LIMIT 1;
 SELECT vr.* INTO run FROM validation_runs vr JOIN processing_jobs j ON j.id=vr.job_id AND j.current_attempt_id=vr.processing_attempt_id
  JOIN processing_job_attempts a ON a.id=vr.processing_attempt_id AND a.processing_job_id=j.id AND a.status='Succeeded'
  WHERE vr.revision_id=rv.id AND j.kind='Geometry' AND j.status='Succeeded' ORDER BY vr.created_at DESC,vr.id DESC LIMIT 1;
 SELECT count(*) INTO blockers FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical');
 IF job.id IS NULL THEN missing:=array_append(missing,'PROCESSING_REQUIRED');
 ELSIF run.id IS NULL THEN missing:=array_append(missing,'GEOMETRY_VALIDATION_REQUIRED');
 ELSE
  IF run.outcome<>'Passed' THEN missing:=array_append(missing,'QA_NOT_PASSED');END IF;
  IF blockers>0 THEN missing:=array_append(missing,'QA_BLOCKERS_PRESENT');END IF;
 END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('revisionId',rv.id,'buildingId',rv.building_id,'revisionStatus',rv.status,
  'geometryJob',CASE WHEN job.id IS NULL THEN NULL ELSE jsonb_build_object('jobId',job.id,'status',job.status,'currentAttemptId',job.current_attempt_id) END,
  'validationRunId',run.id,'outcome',run.outcome,'blockerCount',COALESCE(blockers,0),
  'issueCounts',COALESCE((SELECT jsonb_object_agg(severity,n) FROM (SELECT severity,count(*) n FROM revision_issues WHERE validation_run_id=run.id GROUP BY severity) c),'{}'),
  'readyForScenario',COALESCE(rv.status='ReadyForScenario' AND run.outcome='Passed' AND blockers=0,false),'missing',to_jsonb(missing),
  'latestJobs',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',k.kind,'jobId',k.id,'status',k.status,'scenarioVersionId',k.scenario_version_id,'createdAt',k.created_at) ORDER BY k.kind)
    FROM (SELECT DISTINCT ON (kind) * FROM processing_jobs WHERE revision_id=rv.id ORDER BY kind,created_at DESC,id DESC) k),'[]'),
  'confirmations',COALESCE((SELECT jsonb_agg(jsonb_build_object('reviewId',x.id,'scenarioVersionId',x.scenario_version_id,'validationRunId',x.validation_run_id,'reviewedAt',x.reviewed_at) ORDER BY x.reviewed_at DESC,x.id DESC)
    FROM (SELECT * FROM revision_reviews WHERE revision_id=rv.id AND action='ConfirmForTraining' ORDER BY reviewed_at DESC,id DESC LIMIT 50) x),'[]')));
END $$;

CREATE INDEX IF NOT EXISTS audit_logs_target_history ON audit_logs(target_entity,target_id,created_at DESC,id DESC);

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM review_finding_gate_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
