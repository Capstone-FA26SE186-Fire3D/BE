-- #67: PlatformAdmin training and AI analytics for one organization, and the content-review rejection reason for audit detail.
-- Both are read-only SECURITY DEFINER functions owned by the gate owner; API roles only get EXECUTE.
CREATE TEMP TABLE admin_org_analytics_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO admin_org_analytics_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- Same aggregates as the organization's own report (analytics_training/analytics_ai_usage). Inactive organizations are included;
-- deleted or unknown ones are 404.
CREATE FUNCTION analytics_organization_gate(p_actor uuid,p_family uuid,p_org uuid,p_from timestamptz,p_to timestamptz,p_active_seconds integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;org organizations;as_of timestamptz:=transaction_timestamp();
BEGIN
 IF p_from IS NULL OR p_to IS NULL OR p_to<=p_from OR p_to-p_from>interval '90 days' OR p_active_seconds NOT BETWEEN 10 AND 3600 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO org FROM organizations WHERE id=p_org AND deleted_at IS NULL;
 IF org.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('asOf',as_of,'from',p_from,'to',p_to,
  'organization',jsonb_build_object('id',org.id,'name',org.name,'isActive',org.is_active))
  ||(analytics_training(org.id,p_from,p_to,as_of,p_active_seconds)-'byOrganization')
  ||jsonb_build_object('aiUsage',analytics_ai_usage(org.id,p_from,p_to,as_of)));
END $$;

-- Rejection reason of a content review, only once the review is Rejected. Audit rows keep the review id, not the text.
CREATE FUNCTION audit_review_reason(p_review uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT reason FROM scenario_content_reviews WHERE id=p_review AND status='Rejected'
$$;

DO $grants$ DECLARE r text;sig text;s record;BEGIN
 FOREACH sig IN ARRAY ARRAY['analytics_organization_gate(uuid,uuid,uuid,timestamp with time zone,timestamp with time zone,integer)','audit_review_reason(uuid)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
  FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I',sig,r);END IF;END LOOP;
 END LOOP;
 SELECT * INTO s FROM admin_org_analytics_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
