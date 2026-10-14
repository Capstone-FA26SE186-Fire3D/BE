-- QR status contract (issue #84): resolve tells a revoked code (410 QR_REVOKED) from an unknown one and reports the Building
-- service state (serviceStatus Active|Expired|NotPurchased, serviceEndsAt, canStartNewSessions); the QR list returns
-- replacedId for codes created by rotation. Owner and EXECUTE grants are unchanged by OR REPLACE.
CREATE TEMP TABLE qr_status_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO qr_status_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION building_qr_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;q building_qr_codes;n building_qr_codes;stamp timestamptz:=clock_timestamp();result jsonb;service text;service_ends timestamptz;has_access boolean;
BEGIN
 IF p_action NOT IN('Create','List','Rotate','Revoke','Resolve') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_action='Resolve' THEN
  -- Unknown tokens, other tenants and archived Buildings stay an indistinguishable 404; only a holder of a code of a
  -- visible Building learns that it was revoked (410), which never reveals the replacement token.
  SELECT * INTO q FROM building_qr_codes WHERE token_hash=p_input->>'tokenHash';
  SELECT * INTO b FROM buildings WHERE id=q.building_id;
  IF q.id IS NULL OR b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
   OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','QR_NOT_FOUND','status',404);END IF;
  IF q.status<>'Active' THEN RETURN jsonb_build_object('code','QR_REVOKED','status',410);END IF;
  -- Service mirrors the start gate: only a paid Active entitlement in its window allows new sessions. Expired service
  -- still resolves so the landing can explain why; running sessions are unaffected either way.
  SELECT e.ends_at INTO service_ends FROM service_entitlements e WHERE e.building_id=b.id AND e.organization_id=b.organization_id AND e.status='Active'
   AND e.payment_transaction_id IS NOT NULL AND e.starts_at<=stamp AND e.ends_at>stamp ORDER BY e.ends_at DESC,e.id LIMIT 1;
  IF service_ends IS NOT NULL THEN service:='Active';
  ELSIF EXISTS(SELECT 1 FROM service_entitlements e WHERE e.building_id=b.id AND e.organization_id=b.organization_id AND e.payment_transaction_id IS NOT NULL) THEN
   service:='Expired';
   SELECT max(e.ends_at) INTO service_ends FROM service_entitlements e WHERE e.building_id=b.id AND e.organization_id=b.organization_id AND e.payment_transaction_id IS NOT NULL AND e.starts_at<=stamp;
  ELSE service:='NotPurchased';END IF;
  has_access:=actor.role<>'Trainee' OR b.visibility='Public' OR EXISTS(SELECT 1 FROM building_participation_grants WHERE building_id=b.id AND trainee_user_id=p_actor AND access_revision=b.access_revision);
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'buildingName',b.name,'visibility',b.visibility,'hasAccess',has_access,
   'serviceStatus',service,'serviceEndsAt',service_ends,'canStartNewSessions',service='Active' AND has_access));
 END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO b FROM buildings WHERE id=p_resource;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='List' THEN
  IF COALESCE((p_input->>'page')::int,1)<1 OR COALESCE((p_input->>'pageSize')::int,20) NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  SELECT COALESCE(jsonb_agg(jsonb_build_object('id',x.id,'label',x.label,'status',x.status,'createdAt',x.created_at,'revokedAt',x.revoked_at,'replacedBy',x.replaced_by,
   'replacedId',(SELECT p.id FROM building_qr_codes p WHERE p.replaced_by=x.id)) ORDER BY x.created_at DESC,x.id),'[]')
   INTO result FROM (SELECT * FROM building_qr_codes WHERE building_id=b.id ORDER BY created_at DESC,id
    OFFSET (COALESCE((p_input->>'page')::int,1)-1)::bigint*COALESCE((p_input->>'pageSize')::int,20) LIMIT COALESCE((p_input->>'pageSize')::int,20)) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',(SELECT count(*) FROM building_qr_codes WHERE building_id=b.id),
   'page',COALESCE((p_input->>'page')::int,1),'pageSize',COALESCE((p_input->>'pageSize')::int,20)));
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF p_action IN('Rotate','Revoke') THEN
  SELECT * INTO q FROM building_qr_codes WHERE id=(p_input->>'qrId')::uuid AND building_id=b.id FOR UPDATE;
  IF q.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF q.status<>'Active' THEN RETURN jsonb_build_object('code','QR_ALREADY_REVOKED','status',409);END IF;
 END IF;
 IF p_action='Create' AND EXISTS(SELECT 1 FROM building_qr_codes WHERE building_id=b.id AND status='Active') THEN RETURN jsonb_build_object('code','QR_ALREADY_EXISTS','status',409);END IF;
 -- The old code is revoked before its replacement is inserted so the one-Active index holds; replaced_by is set afterwards.
 IF p_action IN('Rotate','Revoke') THEN UPDATE building_qr_codes SET status='Revoked',revoked_at=stamp WHERE id=q.id;END IF;
 IF p_action IN('Create','Rotate') THEN
  IF p_input->>'tokenHash' !~ '^[0-9a-f]{64}$' OR p_input->>'tokenHash' IS NULL OR length(COALESCE(p_input->>'label',''))>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  INSERT INTO building_qr_codes(id,building_id,organization_id,token_hash,label,status,created_by,created_at)
  VALUES(gen_random_uuid(),b.id,b.organization_id,p_input->>'tokenHash',COALESCE(NULLIF(btrim(p_input->>'label'),''),q.label),'Active',p_actor,stamp) RETURNING * INTO n;
 END IF;
 IF p_action='Rotate' THEN UPDATE building_qr_codes SET replaced_by=n.id WHERE id=q.id;END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','BuildingQrCode',COALESCE(n.id,q.id),gen_random_uuid(),jsonb_build_object('operation',p_action,'previousId',q.id),now());
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('id',COALESCE(n.id,q.id),'buildingId',b.id,'label',COALESCE(n.label,q.label),'status',CASE WHEN p_action='Revoke' THEN 'Revoked' ELSE 'Active' END,'replacedId',CASE WHEN p_action='Rotate' THEN q.id END));
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM qr_status_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
