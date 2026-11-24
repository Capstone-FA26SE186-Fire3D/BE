-- BE#213 (FR-PLAN-04/05/06): fire infrastructure (equipment, exits, assembly points, water sources) per IFC revision, separate from
-- scenarios, versioned and locked once thẩm định passes.
-- * fire_infrastructures: one row per version of a revision. Draft -> Submitted -> Approved | Rejected. Only a Draft is edited; a
--   decided version never changes and nothing is deleted. A fix after a decision is a new version, copied from the newest one, and
--   reviewed again. At most one open (Draft or Submitted) version per revision. row_revision is the ETag.
-- * items[] are editor objects (fet3d.editor/1 object shape) plus category Equipment | WaterSource | Exit | AssemblyPoint; Exit and
--   AssemblyPoint are Goal objects, Equipment and WaterSource are Equipment objects with a fixed (building) capability.
-- * fire_infrastructure_gate: List/Create/Get/Update/Submit for the Building's OrganizationUser or PlatformAdmin.
-- * Scenario drafts reference an Approved version of the same revision (scenario_drafts.infrastructure_id, set at creation). Such a
--   draft must not hold infrastructure objects (every Goal, and Equipment other than scenario items): 422
--   SCENARIO_CONTAINS_INFRASTRUCTURE. Snapshot freezes the scenario objects followed by the infrastructure items in state_snapshot, so
--   scenario_hash, reviews, the package worker (scenario.json, fet3d.package/1 unchanged) and the legacy projections see one document.
--   Drafts created before #213 (infrastructure_id NULL) keep their own objects.
-- * expert_review_gate: kind Infrastructure joins the shared FireSafetyExpert queue; notifications FireInfrastructureApproved/Rejected.
CREATE TEMP TABLE fire_infrastructure_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO fire_infrastructure_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE TABLE fire_infrastructures(
 id uuid PRIMARY KEY,
 revision_id uuid NOT NULL REFERENCES revisions(id),
 building_id uuid NOT NULL REFERENCES buildings(id),
 organization_id uuid NOT NULL REFERENCES organizations(id),
 version_number integer NOT NULL CHECK(version_number>0),
 status text NOT NULL DEFAULT 'Draft' CHECK(status IN('Draft','Submitted','Approved','Rejected')),
 row_revision bigint NOT NULL DEFAULT 1 CHECK(row_revision>0),
 geometry jsonb CHECK(geometry IS NULL OR jsonb_typeof(geometry)='object'),
 runtime_version varchar(32),
 items jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(items)='array'),
 content_hash varchar(64),
 created_by uuid NOT NULL REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 submitted_by uuid REFERENCES users(id),
 submitted_at timestamptz,
 decided_by uuid REFERENCES users(id),
 decided_at timestamptz,
 decision_reason text CHECK(decision_reason IS NULL OR length(decision_reason) BETWEEN 1 AND 2000),
 UNIQUE(revision_id,version_number),
 CONSTRAINT fire_infrastructure_lifecycle CHECK(
  (status='Draft' AND submitted_at IS NULL AND decided_at IS NULL AND decision_reason IS NULL)
  OR (status='Submitted' AND submitted_by IS NOT NULL AND submitted_at IS NOT NULL AND content_hash IS NOT NULL AND decided_at IS NULL AND decision_reason IS NULL)
  OR (status='Approved' AND submitted_at IS NOT NULL AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND decision_reason IS NULL)
  OR (status='Rejected' AND submitted_at IS NOT NULL AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND decision_reason IS NOT NULL))
);
CREATE UNIQUE INDEX fire_infrastructures_one_open ON fire_infrastructures(revision_id) WHERE status IN('Draft','Submitted');
CREATE INDEX fire_infrastructures_queue ON fire_infrastructures(status,submitted_at);

-- A decided version is the lock: no change and no delete. Lifecycle moves only forward.
CREATE FUNCTION guard_fire_infrastructure() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'fire infrastructure versions are never deleted';END IF;
 IF OLD.status IN('Approved','Rejected') THEN RAISE EXCEPTION 'a decided fire infrastructure version is locked';END IF;
 IF NEW.id<>OLD.id OR NEW.revision_id<>OLD.revision_id OR NEW.building_id<>OLD.building_id OR NEW.organization_id<>OLD.organization_id
  OR NEW.version_number<>OLD.version_number OR NEW.created_by<>OLD.created_by OR NEW.created_at<>OLD.created_at THEN
  RAISE EXCEPTION 'fire infrastructure identity is immutable';END IF;
 IF NOT ((OLD.status='Draft' AND NEW.status IN('Draft','Submitted')) OR (OLD.status='Submitted' AND NEW.status IN('Approved','Rejected'))) THEN
  RAISE EXCEPTION 'fire infrastructure status cannot move from % to %',OLD.status,NEW.status;END IF;
 IF OLD.status='Submitted' AND (NEW.items IS DISTINCT FROM OLD.items OR NEW.geometry IS DISTINCT FROM OLD.geometry OR NEW.runtime_version IS DISTINCT FROM OLD.runtime_version) THEN
  RAISE EXCEPTION 'submitted fire infrastructure content is frozen';END IF;
 IF NEW.row_revision<=OLD.row_revision THEN RAISE EXCEPTION 'fire infrastructure row_revision must grow';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER fire_infrastructure_guard BEFORE UPDATE OR DELETE ON fire_infrastructures FOR EACH ROW EXECUTE FUNCTION guard_fire_infrastructure();

ALTER TABLE scenario_drafts ADD COLUMN infrastructure_id uuid REFERENCES fire_infrastructures(id);
ALTER TABLE scenario_versions ADD COLUMN infrastructure_id uuid REFERENCES fire_infrastructures(id);

-- Scenario objects that belong to the fire infrastructure: every Goal (exit, assembly point) and every Equipment except the items a
-- scenario places itself (wet towel, belongings bait). Shared by UpdateDraft/Snapshot here and by the API's validate.
CREATE FUNCTION scenario_infrastructure_conflicts(p_state jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT COALESCE(jsonb_agg(jsonb_build_object('code','SCENARIO_CONTAINS_INFRASTRUCTURE','path','$.objects['||(e.n-1)||']',
   'message',CASE WHEN e.o->>'kind'='Goal' THEN 'Exits and assembly points come from the fire infrastructure; remove this Goal.'
    ELSE 'Fixed equipment comes from the fire infrastructure; a scenario may only place scenario items (item.towel, bait.belongings).' END) ORDER BY e.n),'[]'::jsonb)
 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_state->'objects')='array' THEN p_state->'objects' ELSE '[]'::jsonb END) WITH ORDINALITY e(o,n)
 WHERE jsonb_typeof(e.o)='object' AND (e.o->>'kind'='Goal' OR (e.o->>'kind'='Equipment' AND COALESCE(e.o->'capability'->>'id','') NOT IN('item.towel','bait.belongings')))
$$;

-- The frozen document: scenario objects first (their paths stay the same), then the infrastructure items without category.
CREATE FUNCTION fire_infrastructure_merge(p_state jsonb,p_items jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_set(p_state,'{objects}',
  (CASE WHEN jsonb_typeof(p_state->'objects')='array' THEN p_state->'objects' ELSE '[]'::jsonb END)
  ||COALESCE((SELECT jsonb_agg(i-'category' ORDER BY n) FROM jsonb_array_elements(COALESCE(p_items,'[]'::jsonb)) WITH ORDINALITY x(i,n)),'[]'::jsonb),true)
$$;

-- API validate: conflicts of the draft state and the merged document the snapshot would freeze.
CREATE FUNCTION scenario_infrastructure_view(p_infrastructure uuid,p_state jsonb) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('conflicts',scenario_infrastructure_conflicts(p_state),'state',fire_infrastructure_merge(p_state,f.items),'status',f.status)
 FROM fire_infrastructures f WHERE f.id=p_infrastructure
$$;

CREATE FUNCTION fire_infrastructure_json(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('id',f.id,'revisionId',f.revision_id,'buildingId',f.building_id,'buildingName',b.name,'organizationId',f.organization_id,
  'version',f.version_number,'status',f.status,'revision',f.row_revision,'geometry',f.geometry,'runtimeVersion',f.runtime_version,'items',f.items,
  'contentHash',f.content_hash,'createdBy',f.created_by,'createdAt',f.created_at,'updatedAt',f.updated_at,
  'submittedBy',f.submitted_by,'submittedByName',su.full_name,'submittedAt',f.submitted_at,
  'decidedBy',f.decided_by,'decidedByName',du.full_name,'decidedAt',f.decided_at,'decisionReason',f.decision_reason,
  'scenarioDraftCount',(SELECT count(*) FROM scenario_drafts d WHERE d.infrastructure_id=f.id))
 FROM fire_infrastructures f JOIN buildings b ON b.id=f.building_id
 LEFT JOIN users su ON su.id=f.submitted_by LEFT JOIN users du ON du.id=f.decided_by WHERE f.id=p_id
$$;

CREATE FUNCTION fire_infrastructure_content_hash(f fire_infrastructures) RETURNS text LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$
 SELECT fet3d_jsonb_payload_hash(jsonb_build_object('geometry',f.geometry,'runtimeVersion',f.runtime_version,'items',f.items))
$$;

CREATE FUNCTION fire_infrastructure_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;b buildings;r revisions;f fire_infrastructures;prev fire_infrastructures;receipt readiness_command_receipts;h text;result jsonb;n integer;stamp timestamptz;
BEGIN
 IF p_action NOT IN('List','Create','Get','Update','Submit') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role::text NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action IN('List','Create') THEN SELECT * INTO r FROM revisions WHERE id=p_resource;
 ELSE SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource;SELECT * INTO r FROM revisions WHERE id=f.revision_id;END IF;
 SELECT * INTO b FROM buildings WHERE id=r.building_id;
 IF r.id IS NULL OR b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
  OR (actor.role::text='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;

 IF p_action='List' THEN
  SELECT COALESCE(jsonb_agg(fire_infrastructure_json(x.id) ORDER BY x.version_number DESC),'[]'::jsonb) INTO result FROM fire_infrastructures x WHERE x.revision_id=r.id;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('revisionId',r.id,'items',result));
 ELSIF p_action='Get' THEN
  RETURN jsonb_build_object('code','OK','result',fire_infrastructure_json(f.id));
 END IF;

 IF p_action IN('Create','Submit') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  -- The seed computed by the API from equipment decisions is not part of the request, so a replay never conflicts on it.
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input-'seed','expected',p_expected));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:fire-infrastructure:'||p_actor||':'||p_action||':'||p_key,0));
  SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation='FireInfrastructure'||p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN receipt.result;
  END IF;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 stamp:=clock_timestamp();

 IF p_action='Create' THEN
  SELECT * INTO prev FROM fire_infrastructures WHERE revision_id=r.id AND status IN('Draft','Submitted') LIMIT 1;
  IF prev.id IS NOT NULL THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_OPEN_VERSION_EXISTS','status',409,
   'errors',jsonb_build_object('id',jsonb_build_array(prev.id::text)));END IF;
  SELECT * INTO prev FROM fire_infrastructures WHERE revision_id=r.id ORDER BY version_number DESC LIMIT 1;
  n:=COALESCE(prev.version_number,0)+1;
  INSERT INTO fire_infrastructures(id,revision_id,building_id,organization_id,version_number,geometry,runtime_version,items,created_by,created_at,updated_at)
  VALUES(gen_random_uuid(),r.id,b.id,b.organization_id,n,
   CASE WHEN prev.id IS NOT NULL THEN prev.geometry ELSE NULLIF(p_input->'seed'->'geometry','null'::jsonb) END,
   CASE WHEN prev.id IS NOT NULL THEN prev.runtime_version ELSE p_input->'seed'->>'runtimeVersion' END,
   CASE WHEN prev.id IS NOT NULL THEN prev.items WHEN jsonb_typeof(p_input->'seed'->'items')='array' THEN p_input->'seed'->'items' ELSE '[]'::jsonb END,
   p_actor,stamp,stamp) RETURNING * INTO f;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','FireInfrastructure',f.id,gen_random_uuid(),
   jsonb_build_object('revisionId',r.id,'version',n,'copiedFrom',prev.id,'itemCount',jsonb_array_length(f.items)),now());
 ELSE
  SELECT * INTO f FROM fire_infrastructures WHERE id=f.id FOR UPDATE;
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  IF p_expected<>f.row_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF f.status='Approved' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_LOCKED','status',409);END IF;
  IF f.status<>'Draft' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_DRAFT','status',409);END IF;
  IF p_action='Update' THEN
   -- Shape and categories are validated by the API; the gate keeps the container types honest.
   IF jsonb_typeof(p_input->'items') IS DISTINCT FROM 'array' OR (p_input->'geometry' IS NOT NULL AND jsonb_typeof(p_input->'geometry') NOT IN('object','null'))
    THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
   UPDATE fire_infrastructures SET items=p_input->'items',geometry=NULLIF(p_input->'geometry','null'::jsonb),runtime_version=p_input->>'runtimeVersion',
    row_revision=row_revision+1,updated_at=stamp WHERE id=f.id RETURNING * INTO f;
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
   VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','FireInfrastructure',f.id,gen_random_uuid(),
    jsonb_build_object('revision',f.row_revision,'itemCount',jsonb_array_length(f.items),'contentHash',fire_infrastructure_content_hash(f)),now());
   RETURN jsonb_build_object('code','OK','result',fire_infrastructure_json(f.id));
  END IF;
  -- Submit: the API validated exactly this revision of the content (If-Match) before calling.
  IF jsonb_array_length(f.items)=0 OR f.geometry IS NULL OR f.runtime_version IS NULL THEN
   RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_INVALID','status',422);END IF;
  UPDATE fire_infrastructures SET status='Submitted',submitted_by=p_actor,submitted_at=stamp,content_hash=fire_infrastructure_content_hash(f),
   row_revision=row_revision+1,updated_at=stamp WHERE id=f.id RETURNING * INTO f;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','FireInfrastructure',f.id,gen_random_uuid(),jsonb_build_object('status','Draft'),
   jsonb_build_object('status','Submitted','contentHash',f.content_hash,'submittedBy',p_actor,'submittedAt',stamp),now());
 END IF;
 result:=jsonb_build_object('code','OK','result',fire_infrastructure_json(f.id));
 INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'FireInfrastructure'||p_action,p_key,h,result);
 RETURN result;
END $$;

-- Notifications for infrastructure decisions.
ALTER TABLE organization_notifications ADD COLUMN fire_infrastructure_id uuid REFERENCES fire_infrastructures(id) ON DELETE RESTRICT;
ALTER TABLE organization_notifications DROP CONSTRAINT check_org_notification_type;
ALTER TABLE organization_notifications ADD CONSTRAINT check_org_notification_type CHECK (notification_type IN('BuildingServiceExpiring','ContentReviewApproved','ContentReviewRejected','FireInfrastructureApproved','FireInfrastructureRejected'));
ALTER TABLE organization_notifications DROP CONSTRAINT check_org_notification_reference;
ALTER TABLE organization_notifications ADD CONSTRAINT check_org_notification_reference CHECK (
 (notification_type='BuildingServiceExpiring' AND entitlement_id IS NOT NULL AND reference_ends_at IS NOT NULL AND content_review_id IS NULL AND scenario_version_id IS NULL AND fire_infrastructure_id IS NULL)
 OR (notification_type IN('ContentReviewApproved','ContentReviewRejected') AND content_review_id IS NOT NULL AND scenario_version_id IS NOT NULL AND entitlement_id IS NULL AND reference_ends_at IS NULL AND fire_infrastructure_id IS NULL)
 OR (notification_type IN('FireInfrastructureApproved','FireInfrastructureRejected') AND fire_infrastructure_id IS NOT NULL AND content_review_id IS NULL AND scenario_version_id IS NULL AND entitlement_id IS NULL AND reference_ends_at IS NULL));

CREATE OR REPLACE FUNCTION validate_organization_notification_write()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.recipient_user_id IS DISTINCT FROM NEW.recipient_user_id
        OR OLD.building_id IS DISTINCT FROM NEW.building_id
        OR OLD.entitlement_id IS DISTINCT FROM NEW.entitlement_id
        OR OLD.content_review_id IS DISTINCT FROM NEW.content_review_id
        OR OLD.scenario_version_id IS DISTINCT FROM NEW.scenario_version_id
        OR OLD.fire_infrastructure_id IS DISTINCT FROM NEW.fire_infrastructure_id
        OR OLD.notification_type IS DISTINCT FROM NEW.notification_type
        OR OLD.reference_ends_at IS DISTINCT FROM NEW.reference_ends_at
    ) THEN
        RAISE EXCEPTION 'notification identity and tenant scope are immutable';
    END IF;
    IF NOT EXISTS (
            SELECT 1 FROM public.users AS recipient
             WHERE recipient.id = NEW.recipient_user_id AND recipient.organization_id = NEW.organization_id AND recipient.role = 'OrganizationUser')
       OR NOT EXISTS (SELECT 1 FROM public.buildings AS building WHERE building.id = NEW.building_id AND building.organization_id = NEW.organization_id) THEN
        RAISE EXCEPTION 'notification recipient and Building must belong to the organization';
    END IF;
    IF NEW.notification_type = 'BuildingServiceExpiring' THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.service_entitlements AS entitlement
             WHERE entitlement.id = NEW.entitlement_id AND entitlement.organization_id = NEW.organization_id
               AND entitlement.building_id = NEW.building_id AND entitlement.ends_at = NEW.reference_ends_at) THEN
            RAISE EXCEPTION 'expiry notification recipient, Building and entitlement must share the organization and expiry';
        END IF;
    ELSIF NEW.notification_type IN ('ContentReviewApproved','ContentReviewRejected') THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.scenario_content_reviews AS review JOIN public.scenario_versions AS version ON version.id = review.scenario_version_id
             WHERE review.id = NEW.content_review_id AND version.id = NEW.scenario_version_id AND version.building_id = NEW.building_id
               AND review.status = CASE NEW.notification_type WHEN 'ContentReviewApproved' THEN 'Approved' ELSE 'Rejected' END) THEN
            RAISE EXCEPTION 'review notification must match a decided review of the Building';
        END IF;
    ELSIF NEW.notification_type IN ('FireInfrastructureApproved','FireInfrastructureRejected') THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.fire_infrastructures AS infrastructure
             WHERE infrastructure.id = NEW.fire_infrastructure_id AND infrastructure.building_id = NEW.building_id
               AND infrastructure.status = CASE NEW.notification_type WHEN 'FireInfrastructureApproved' THEN 'Approved' ELSE 'Rejected' END) THEN
            RAISE EXCEPTION 'infrastructure notification must match a decided infrastructure version of the Building';
        END IF;
    ELSE
        RAISE EXCEPTION 'unknown organization notification type';
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION expert_infrastructure_item(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('reviewId',f.id,'kind','Infrastructure','status',f.status,'revision',f.row_revision,'infrastructureId',f.id,'revisionId',f.revision_id,
  'contentHash',f.content_hash,'submittedBy',f.submitted_by,'submittedByName',su.full_name,'submittedAt',f.submitted_at,
  'decidedBy',f.decided_by,'decidedByName',du.full_name,'decidedAt',f.decided_at,'reason',f.decision_reason,
  'organizationId',f.organization_id,'organizationName',o.name,'buildingId',f.building_id,'buildingName',b.name,'versionNumber',f.version_number)
 FROM fire_infrastructures f JOIN buildings b ON b.id=f.building_id JOIN organizations o ON o.id=f.organization_id
 LEFT JOIN users su ON su.id=f.submitted_by LEFT JOIN users du ON du.id=f.decided_by WHERE f.id=p_id
$$;

-- Same gate as 20261124090100_ExpertContentReview with kind Infrastructure added: List merges both kinds (kind NULL = both), and
-- Get/Preview/Approve/Reject take a scenario review id or a submitted infrastructure version id.
CREATE OR REPLACE FUNCTION expert_review_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;r scenario_content_reviews;v scenario_versions;b buildings;s scenarios;f fire_infrastructures;receipt readiness_command_receipts;
 requested_kind text;requested_status text;page_number integer;page_size integer;total integer;result jsonb;h text;before_values jsonb;reason_text text;stamp timestamptz;
BEGIN
 IF p_action NOT IN('List','Get','Preview','Approve','Reject') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 -- Only experts; PlatformAdmin has no thẩm định path, not even while no expert exists (FR-PLAN-05).
 IF actor.role::text<>'FireSafetyExpert' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;

 IF p_action='List' THEN
  requested_kind:=p_input->>'kind';requested_status:=COALESCE(p_input->>'status','Submitted');
  page_number:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF (requested_kind IS NOT NULL AND requested_kind NOT IN('Scenario','Infrastructure')) OR requested_status NOT IN('Submitted','Approved','Rejected')
   OR page_number<1 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- Waiting items oldest first (first come, first reviewed); decided items newest decision first.
  WITH q AS (
   SELECT 'Scenario' AS kind,id,submitted_at,reviewed_at AS decided_at FROM scenario_content_reviews
    WHERE status=requested_status AND (requested_kind IS NULL OR requested_kind='Scenario')
   UNION ALL
   SELECT 'Infrastructure',id,submitted_at,decided_at FROM fire_infrastructures
    WHERE status=requested_status AND (requested_kind IS NULL OR requested_kind='Infrastructure')),
  paged AS (
   SELECT kind,id,row_number() OVER(ORDER BY CASE WHEN requested_status='Submitted' THEN submitted_at END,
    CASE WHEN requested_status<>'Submitted' THEN decided_at END DESC,id) rn FROM q)
  SELECT (SELECT count(*) FROM q),
   COALESCE((SELECT jsonb_agg(CASE WHEN p.kind='Scenario' THEN expert_review_item(p.id) ELSE expert_infrastructure_item(p.id) END ORDER BY p.rn)
    FROM paged p WHERE p.rn>(page_number::bigint-1)*page_size AND p.rn<=page_number::bigint*page_size),'[]'::jsonb)
  INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size));
 END IF;

 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource;
 IF r.id IS NULL THEN
  -- Infrastructure versions enter the queue when submitted; a Draft is the organization's own work and stays invisible.
  SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource AND status<>'Draft';
  SELECT * INTO b FROM buildings WHERE id=f.building_id;
  IF f.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p_action='Get' THEN
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('review',expert_infrastructure_item(f.id),
    'content',jsonb_build_object('geometry',f.geometry,'runtimeVersion',f.runtime_version,'items',f.items),'rubric',NULL,'learningObjectives',NULL,'learnerInstructions',NULL));
  ELSIF p_action='Preview' THEN
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'revisionId',f.revision_id));
  END IF;
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('review',p_resource,'input',p_input,'expected',p_expected));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-review:'||p_actor||':'||p_action||':'||p_key,0));
  SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation='Expert'||p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN receipt.result;
  END IF;
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  reason_text:=NULLIF(btrim(p_input->>'reason'),'');
  IF p_action='Reject' AND (reason_text IS NULL OR length(reason_text)>2000) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-2000 characters is required.')));END IF;
  IF p_action='Approve' THEN reason_text:=NULL;END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
  SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource FOR UPDATE;
  IF p_expected<>f.row_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF f.status<>'Submitted' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_PENDING','status',409);END IF;
  IF f.content_hash IS DISTINCT FROM fire_infrastructure_content_hash(f) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
  stamp:=clock_timestamp();
  UPDATE fire_infrastructures SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,decided_by=p_actor,decided_at=stamp,
   decision_reason=reason_text,row_revision=row_revision+1,updated_at=stamp WHERE id=f.id RETURNING * INTO f;
  INSERT INTO organization_notifications(id,organization_id,recipient_user_id,building_id,fire_infrastructure_id,notification_type,title,body,idempotency_key,created_at)
  SELECT gen_random_uuid(),b.organization_id,u.id,b.id,f.id,CASE WHEN f.status='Approved' THEN 'FireInfrastructureApproved' ELSE 'FireInfrastructureRejected' END,
   CASE WHEN f.status='Approved' THEN 'Hạ tầng PCCC đạt thẩm định' ELSE 'Hạ tầng PCCC chưa đạt thẩm định' END,
   'Hạ tầng PCCC (phiên bản '||f.version_number||') của công trình '||b.name||
   CASE WHEN f.status='Approved' THEN ' đã đạt thẩm định của Chuyên viên PCCC và đã được khóa.' ELSE ' chưa đạt thẩm định của Chuyên viên PCCC. Lý do: '||f.decision_reason END,
   'fire-infrastructure:'||f.id,stamp
  FROM users u WHERE u.id=f.submitted_by AND u.role='OrganizationUser' AND u.organization_id=b.organization_id AND u.is_active AND u.deleted_at IS NULL
  ON CONFLICT (idempotency_key) DO NOTHING;
  result:=jsonb_build_object('code','OK','result',expert_infrastructure_item(f.id));
  INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Expert'||p_action,p_key,h,result);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Reject' THEN 'Reject'::audit_action_enum ELSE 'Update'::audit_action_enum END,
   'FireInfrastructure',f.id,gen_random_uuid(),jsonb_build_object('status','Submitted'),
   jsonb_build_object('auditContractVersion',1,'status',f.status,'version',f.version_number,'contentHash',f.content_hash,'submittedBy',f.submitted_by,
    'decidedBy',f.decided_by,'decidedAt',f.decided_at,'decidedByRole','FireSafetyExpert'),now());
  RETURN result;
 END IF;

 SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id;
 SELECT * INTO b FROM buildings WHERE id=v.building_id;
 IF r.id IS NULL OR v.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;

 IF p_action='Get' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('review',expert_review_item(r.id),'content',v.state_snapshot,'rubric',v.rubric,
   'learningObjectives',v.learning_objectives,'learnerInstructions',v.learner_instructions));
 ELSIF p_action='Preview' THEN
  -- The caller signs the 3D preview of exactly this revision; nothing else of the organization is opened.
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'revisionId',v.revision_id));
 END IF;

 -- Approve / Reject
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('review',p_resource,'input',p_input,'expected',p_expected));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-review:'||p_actor||':'||p_action||':'||p_key,0));
 SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation='Expert'||p_action AND idempotency_key=p_key;
 IF receipt.result IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  RETURN receipt.result;
 END IF;
 IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
 reason_text:=NULLIF(btrim(p_input->>'reason'),'');
 IF p_action='Reject' AND (reason_text IS NULL OR length(reason_text)>2000) THEN
  RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-2000 characters is required.')));END IF;
 IF p_action='Approve' THEN reason_text:=NULL;END IF;
 -- Same lock order as scenario_readiness_gate: Building, then the review row.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource FOR UPDATE;
 IF p_expected<>(CASE WHEN r.status='Submitted' THEN 1 ELSE 2 END) THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
 IF r.status<>'Submitted' THEN RETURN jsonb_build_object('code','CONTENT_REVIEW_NOT_PENDING','status',409);END IF;
 IF r.content_hash<>v.scenario_hash OR r.rubric_hash<>fet3d_jsonb_payload_hash(v.rubric) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
 before_values:=jsonb_build_object('status',r.status,'reviewId',r.id,'scenarioVersionId',v.id,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,'reviewedBy',r.reviewed_by,'reviewedAt',r.reviewed_at);
 stamp:=clock_timestamp();
 UPDATE scenario_content_reviews SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,reviewed_by=p_actor,reviewed_at=stamp,reason=reason_text
  WHERE id=r.id RETURNING * INTO r;
 SELECT * INTO s FROM scenarios WHERE id=v.scenario_id;
 -- The submitter hears the result while they are still an active OrganizationUser of that organization.
 INSERT INTO organization_notifications(id,organization_id,recipient_user_id,building_id,content_review_id,scenario_version_id,notification_type,title,body,idempotency_key,created_at)
 SELECT gen_random_uuid(),b.organization_id,u.id,b.id,r.id,v.id,CASE WHEN r.status='Approved' THEN 'ContentReviewApproved' ELSE 'ContentReviewRejected' END,
  CASE WHEN r.status='Approved' THEN 'Tình huống đạt thẩm định' ELSE 'Tình huống chưa đạt thẩm định' END,
  'Tình huống "'||COALESCE(s.name,'')||'" (phiên bản '||v.version_number||') của công trình '||b.name||
  CASE WHEN r.status='Approved' THEN ' đã đạt thẩm định của Chuyên viên PCCC.' ELSE ' chưa đạt thẩm định của Chuyên viên PCCC. Lý do: '||r.reason END,
  'content-review:'||r.id,stamp
 FROM users u WHERE u.id=r.submitted_by AND u.role='OrganizationUser' AND u.organization_id=b.organization_id AND u.is_active AND u.deleted_at IS NULL
 ON CONFLICT (idempotency_key) DO NOTHING;
 result:=jsonb_build_object('code','OK','result',expert_review_item(r.id));
 INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Expert'||p_action,p_key,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Reject' THEN 'Reject'::audit_action_enum ELSE 'Update'::audit_action_enum END,
  'ScenarioVersion',v.id,gen_random_uuid(),before_values,
  jsonb_build_object('auditContractVersion',1,'status',r.status,'reviewId',r.id,'scenarioVersionId',v.id,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,
   'reviewedBy',r.reviewed_by,'reviewedAt',r.reviewed_at,'decidedByRole','FireSafetyExpert'),now());
 RETURN result;
END $$;

-- scenario_authoring_gate as in 20261019130000_PackageContractGates, with two changes for drafts on a fire infrastructure:
--  * CreateDraft takes infrastructureId: an Approved version of the same revision (422 FIRE_INFRASTRUCTURE_NOT_APPROVED /
--    FIRE_INFRASTRUCTURE_REVISION_MISMATCH). Without it the draft is a legacy one (the API requires it for new drafts).
--  * UpdateDraft and Snapshot refuse infrastructure objects in such a draft (422 SCENARIO_CONTAINS_INFRASTRUCTURE); Snapshot freezes
--    the merged document (scenario objects, then the infrastructure items) and records infrastructure_id on the version.
CREATE OR REPLACE FUNCTION scenario_authoring_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_revision bigint DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;s scenarios;d scenario_drafts;v scenario_versions;r revisions;src source_documents;receipt authoring_command_receipts;fi fire_infrastructures;
 h text:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input,'revision',p_revision));result_uuid uuid;n integer;rev bigint;job uuid;pin jsonb;issues jsonb;st jsonb;
BEGIN
 IF p_action<>'UpdateDraft' AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 IF p_action='CreateScenario' THEN SELECT * INTO b FROM buildings WHERE id=p_resource;
 ELSIF p_action='CreateDraft' THEN SELECT * INTO s FROM scenarios WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 ELSIF p_action='BuildPackage' THEN SELECT * INTO v FROM scenario_versions WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=v.scenario_id;SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSE SELECT * INTO d FROM scenario_drafts WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=d.scenario_id;SELECT * INTO b FROM buildings WHERE id=d.building_id; END IF;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF p_key IS NOT NULL THEN PERFORM pg_advisory_xact_lock(hashtextextended('authoring:'||p_actor||':'||p_action||':'||p_key,0)); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF s.id IS NOT NULL THEN SELECT * INTO s FROM scenarios WHERE scenarios.id=s.id FOR UPDATE; END IF;
 SELECT * INTO receipt FROM authoring_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
 IF receipt.result_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  RETURN jsonb_build_object('code','OK','id',receipt.result_id);
 END IF;
 IF p_action='CreateScenario' THEN
  IF NULLIF(btrim(p_input->>'name'),'') IS NULL OR length(p_input->>'name')>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('name',jsonb_build_array('Name must contain 1-255 characters.'))); END IF;
  result_uuid:=gen_random_uuid();INSERT INTO scenarios(id,building_id,organization_id,name,created_by,created_at) VALUES(result_uuid,b.id,b.organization_id,btrim(p_input->>'name'),p_actor,now());
 ELSIF p_action='CreateDraft' THEN
  SELECT * INTO r FROM revisions WHERE revisions.id=(p_input->>'revisionId')::uuid AND building_id=b.id AND organization_id=b.organization_id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('revisionId',jsonb_build_array('Revision must belong to this Building.'))); END IF;
  IF p_input->>'infrastructureId' IS NOT NULL THEN
   SELECT * INTO fi FROM fire_infrastructures WHERE id=(p_input->>'infrastructureId')::uuid AND building_id=b.id;
   IF fi.id IS NULL OR fi.revision_id<>r.id THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_REVISION_MISMATCH','status',422,'errors',jsonb_build_object('infrastructureId',jsonb_build_array('Use a fire infrastructure version of the same revision.'))); END IF;
   IF fi.status<>'Approved' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_APPROVED','status',422,'errors',jsonb_build_object('infrastructureId',jsonb_build_array('Only an Approved (locked) fire infrastructure version can carry scenarios.'))); END IF;
  END IF;
  SELECT COALESCE(max(draft_number),0)+1 INTO n FROM scenario_drafts WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();INSERT INTO scenario_drafts(id,scenario_id,revision_id,organization_id,building_id,draft_number,state,source,created_by,created_at,updated_at,infrastructure_id) VALUES(result_uuid,s.id,r.id,b.organization_id,b.id,n,'{}','manual',p_actor,now(),now(),fi.id);
 ELSIF p_action IN('UpdateDraft','Snapshot') THEN
  SELECT * INTO d FROM scenario_drafts WHERE scenario_drafts.id=p_resource FOR UPDATE;
  SELECT xmin::text::bigint INTO rev FROM scenario_drafts WHERE scenario_drafts.id=p_resource;
  IF p_revision IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428); END IF;
  IF rev<>p_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412); END IF;
  IF d.infrastructure_id IS NOT NULL THEN
   issues:=scenario_infrastructure_conflicts(CASE WHEN p_action='UpdateDraft' THEN p_input ELSE d.state END);
   IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','SCENARIO_CONTAINS_INFRASTRUCTURE','status',422,
    'errors',(SELECT jsonb_object_agg(i->>'path',jsonb_build_array(i->>'message')) FROM jsonb_array_elements(issues) i)); END IF;
  END IF;
  IF p_action='UpdateDraft' THEN
   UPDATE scenario_drafts SET state=p_input,updated_at=now() WHERE scenario_drafts.id=d.id RETURNING xmin::text::bigint INTO rev;
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','ScenarioDraft',d.id,gen_random_uuid(),jsonb_build_object('revision',rev,'stateHash',fet3d_jsonb_payload_hash(p_input)),now());
   RETURN jsonb_build_object('code','OK','revision',rev);
  END IF;
  st:=d.state;
  IF d.infrastructure_id IS NOT NULL THEN
   SELECT * INTO fi FROM fire_infrastructures WHERE id=d.infrastructure_id;
   IF fi.status<>'Approved' OR fi.revision_id<>d.revision_id THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_APPROVED','status',422); END IF;
   IF d.state->>'schemaVersion' IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('A draft on a fire infrastructure uses fet3d.editor/1.'))); END IF;
   st:=fire_infrastructure_merge(d.state,fi.items);
  END IF;
  IF jsonb_typeof(st->'rubric') IS DISTINCT FROM 'object' OR jsonb_typeof(st->'learningObjectives') IS DISTINCT FROM 'array' OR jsonb_array_length(st->'learningObjectives')=0 OR NULLIF(btrim(st->>'learnerInstructions'),'') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Validate the v7 rubric and learner fields before snapshot.'))); END IF;
  issues:=scenario_reference_issues(d.revision_id,st);
  IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Anchor or runtime capability is not accepted for this revision.')));END IF;
  SELECT COALESCE(max(version_number),0)+1 INTO n FROM scenario_versions WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();
  INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,scenario_hash,created_by,created_at,state_snapshot,learning_objectives,learner_instructions,rubric,infrastructure_id)
   VALUES(result_uuid,s.id,d.revision_id,b.id,b.organization_id,n,COALESCE(NULLIF(st->>'name',''),s.name),'7','1',COALESCE((st->>'randomSeed')::bigint,0),(st->'scoringConfig'->>'timeLimitSeconds')::integer,st->'spawnPoints',COALESCE(NULLIF(st->'goals','null'::jsonb),'[]'),st->'hazards',COALESCE(NULLIF(st->'npcs','null'::jsonb),'[]'),COALESCE(NULLIF(st->'blockedElements','null'::jsonb),'[]'),st->'routingConfig',st->'scoringConfig',COALESCE(NULLIF(st->'modePolicy','null'::jsonb),'{}'),COALESCE(NULLIF(st->'safetyThresholds','null'::jsonb),'{}'),COALESCE((st->>'replanIntervalSeconds')::integer,0),fet3d_jsonb_payload_hash(st),p_actor,now(),st,st->'learningObjectives',st->>'learnerInstructions',st->'rubric',d.infrastructure_id);
 ELSIF p_action='BuildPackage' THEN
  IF v.state_snapshot IS NULL THEN RETURN jsonb_build_object('code','SCENARIO_SNAPSHOT_REQUIRED','status',409); END IF;
  SELECT * INTO src FROM source_documents WHERE revision_id=v.revision_id AND upload_verified_at IS NOT NULL;
  IF src.id IS NULL THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
  IF p_input->>'kind' IS NULL OR p_input->>'kind' NOT IN('PlaytestPackage','ReleasePackage') OR NULLIF(p_input->>'buildTarget','') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400); END IF;
  pin:=jsonb_build_object('scenarioVersionId',v.id,'scenarioHash',v.scenario_hash,'rubricHash',fet3d_jsonb_payload_hash(v.rubric),'sourceId',src.id,'sourceHash',src.sha256_hash,'kind',p_input->>'kind','buildTarget',p_input->>'buildTarget');
  SELECT processing_jobs.id INTO result_uuid FROM processing_jobs WHERE revision_id=v.revision_id AND scenario_version_id=v.id AND kind=p_input->>'kind' AND input_hash=fet3d_jsonb_payload_hash(pin);
  IF result_uuid IS NULL THEN
   -- One Queued/Running job per revision (unique index one_live_revision_job): answer 409 with that job instead of a 500.
   SELECT j.id INTO job FROM processing_jobs j WHERE j.revision_id=v.revision_id AND j.status IN('Queued','Running') ORDER BY j.created_at LIMIT 1;
   IF job IS NOT NULL THEN RETURN jsonb_build_object('code','PROCESSING_JOB_ACTIVE','status',409,'errors',jsonb_build_object('jobId',jsonb_build_array(job::text))); END IF;
   result_uuid:=gen_random_uuid();INSERT INTO processing_jobs(id,revision_id,source_document_id,scenario_version_id,kind,job_key,input_hash,input_snapshot,status,created_at) VALUES(result_uuid,v.revision_id,src.id,v.id,p_input->>'kind',gen_random_uuid(),fet3d_jsonb_payload_hash(pin),pin,'Queued',now());
   PERFORM enqueue_integration_outbox_event('package-build:'||result_uuid,'ProcessingJob',result_uuid,'ProcessingJobRequested','1',jsonb_build_object('job_id',result_uuid,'input_hash',fet3d_jsonb_payload_hash(pin),'input_snapshot',pin));
  END IF;
 ELSE RETURN jsonb_build_object('code','AUTHORING_ACTION_INVALID','status',400); END IF;
 INSERT INTO authoring_command_receipts(actor_id,operation,idempotency_key,input_hash,result_id) VALUES(p_actor,p_action,p_key,h,result_uuid);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create',CASE p_action WHEN 'CreateScenario' THEN 'Scenario' WHEN 'CreateDraft' THEN 'ScenarioDraft' WHEN 'Snapshot' THEN 'ScenarioVersion' ELSE 'ProcessingJob' END,result_uuid,gen_random_uuid(),jsonb_build_object('operation',p_action),now());
 RETURN jsonb_build_object('code','OK','id',result_uuid);
END $$;

DO $grants$ DECLARE s record;sig text;r text;t text;BEGIN
 ALTER TABLE fire_infrastructures ENABLE ROW LEVEL SECURITY;
 REVOKE ALL ON fire_infrastructures FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated','fire3d_api','fet3d_backend_executor'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON fire_infrastructures FROM %I',r);END IF;END LOOP;
 GRANT SELECT,INSERT,UPDATE ON fire_infrastructures TO fet3d_ifc_upload_owner;
 CREATE POLICY fire_infrastructure_owner ON fire_infrastructures TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
 -- Pending-registration cleanup deletes users; the RESTRICT foreign key checks must see these rows.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON fire_infrastructures TO fet3d_pending_cleanup_owner;
  CREATE POLICY pending_cleanup_owner ON fire_infrastructures FOR SELECT TO fet3d_pending_cleanup_owner USING(true);
 END IF;
 FOREACH t IN ARRAY ARRAY['revisions','buildings','organizations','users','scenario_drafts','readiness_command_receipts','audit_logs'] LOOP
  EXECUTE format('GRANT SELECT ON %I TO fet3d_ifc_upload_owner',t);
  IF (SELECT relrowsecurity FROM pg_class WHERE oid=format('public.%I',t)::regclass) AND NOT EXISTS(SELECT 1 FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=t
     AND ('fet3d_ifc_upload_owner'=ANY(p.roles) OR 'public'=ANY(p.roles)) AND p.cmd IN('ALL','SELECT')) THEN
   EXECUTE format('CREATE POLICY fire_infrastructure_owner_read ON %I FOR SELECT TO fet3d_ifc_upload_owner USING(true)',t);
  END IF;
 END LOOP;
 GRANT INSERT ON readiness_command_receipts,audit_logs TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['scenario_infrastructure_view(uuid,jsonb)','fire_infrastructure_json(uuid)','fire_infrastructure_content_hash(fire_infrastructures)',
  'fire_infrastructure_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','expert_infrastructure_item(uuid)','guard_fire_infrastructure()',
  'scenario_infrastructure_conflicts(jsonb)','fire_infrastructure_merge(jsonb,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION fire_infrastructure_gate(text,uuid,uuid,uuid,jsonb,text,bigint),scenario_infrastructure_view(uuid,jsonb) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM fire_infrastructure_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
