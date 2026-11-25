-- BE#215 (Flow 1 PC06, design §5; FR-PLAN-07, FR-PLAN-12).
-- * Scenario state fields audience / assumption / workingHours are validated by the API (fet3d.editor/1: optional on legacy drafts,
--   required on drafts built on a fire infrastructure); they live in state and state_snapshot, so nothing new is stored.
-- * publish_release_gate: the definition from 20261019130000_CompareReleasePackageContractFields with one addition: a scenario version
--   on a fire infrastructure needs that infrastructure Approved and Approved situations covering InHours and OffHours on it, else 409
--   PLAN_SCENARIO_COVERAGE_INCOMPLETE (errors.workingHours = the missing values). Versions without an infrastructure are unchanged.
-- * plan_dossier_gate: the "hồ sơ phương án trực quan" of a Building in the order of Mẫu PC06 (header, I, II, III, IV.1, V), read-only,
--   for the Building's OrganizationUser or PlatformAdmin.
CREATE TEMP TABLE plan_dossier_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO plan_dossier_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION publish_release_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;b buildings;r releases;v scenario_versions;p release_packages;pin release_build_provenance;
 cr scenario_content_reviews;rr revision_reviews;vr validation_runs;a revision_artifacts;m revision_artifacts;stamp timestamptz; receipt release_command_receipts; result jsonb; h text; k text; missing text[];
BEGIN
 IF p_action<>'Publish' THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 SELECT * INTO r FROM releases WHERE id=p_resource;
 SELECT * INTO b FROM buildings WHERE id=r.building_id;
 IF b.id IS NULL OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO b FROM buildings WHERE id=b.id FOR UPDATE;
 SELECT * INTO r FROM releases WHERE id=p_resource FOR UPDATE;
 stamp:=clock_timestamp();
 IF NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>stamp) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 k:=btrim(p_key);
 IF k IS NULL OR length(k) NOT BETWEEN 1 AND 128 OR k ~ '[[:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('releaseId',r.id,'request',coalesce(p_input,'{}'::jsonb)));
 SELECT * INTO receipt FROM release_command_receipts WHERE actor_id=p_actor AND operation='Publish' AND idempotency_key=k;
 IF receipt.actor_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN
   IF NOT(k=r.id::text AND p_input='{}'::jsonb AND jsonb_typeof(receipt.result)='boolean' AND receipt.input_hash=fet3d_jsonb_payload_hash(jsonb_build_object('releaseId',r.id))) THEN
    RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);
   END IF;
  END IF;
  RETURN jsonb_build_object('code','OK','result',CASE WHEN jsonb_typeof(receipt.result)='boolean' THEN release_representation(r.id) ELSE receipt.result END);
 END IF;
 IF r.status='Published' THEN
  result:=release_representation(r.id);
  INSERT INTO release_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Publish',k,h,result);
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;
 IF r.status<>'Built' THEN RETURN jsonb_build_object('code','RELEASE_NOT_BUILT','status',409); END IF;
 IF NOT EXISTS(SELECT 1 FROM trainings WHERE release_id=r.id AND scenario_version_id=r.scenario_version_id AND organization_id=b.organization_id AND status='Active') THEN RETURN jsonb_build_object('code','TRAINING_INACTIVE','status',409); END IF;
 IF NOT EXISTS(SELECT 1 FROM service_entitlements e JOIN payment_transactions t ON t.id=e.payment_transaction_id AND t.status='Applied'
  WHERE e.building_id=b.id AND e.organization_id=b.organization_id AND e.status='Active' AND e.starts_at<=stamp AND e.ends_at>stamp)
  THEN RETURN jsonb_build_object('code','BUILDING_ENTITLEMENT_REQUIRED','status',409); END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id AND revision_id=r.revision_id AND building_id=b.id;
 SELECT * INTO pin FROM release_build_provenance WHERE release_id=r.id;
 SELECT * INTO cr FROM scenario_content_reviews WHERE id=pin.content_review_id AND scenario_version_id=v.id AND status='Approved' AND content_hash=v.scenario_hash AND rubric_hash=fet3d_jsonb_payload_hash(v.rubric);
 IF cr.id IS NULL THEN RETURN jsonb_build_object('code','CONTENT_APPROVAL_REQUIRED','status',409); END IF;
 -- BE#215 (FR-PLAN-07): a scenario built on a fire infrastructure is published only while that infrastructure is Approved and it has
 -- at least one Approved situation in working hours and one outside them (Điều 15 khoản 1 điểm d; chú thích (3) Mẫu PC06).
 IF v.infrastructure_id IS NOT NULL THEN
  IF NOT EXISTS(SELECT 1 FROM fire_infrastructures WHERE id=v.infrastructure_id AND status='Approved') THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_APPROVED','status',409); END IF;
  SELECT array_agg(w ORDER BY w) INTO missing FROM unnest(ARRAY['InHours','OffHours']) w WHERE NOT EXISTS(
   SELECT 1 FROM scenario_versions sv JOIN scenario_content_reviews scr ON scr.scenario_version_id=sv.id AND scr.status='Approved' AND scr.content_hash=sv.scenario_hash
   WHERE sv.infrastructure_id=v.infrastructure_id AND sv.state_snapshot->>'workingHours'=w);
  IF missing IS NOT NULL THEN RETURN jsonb_build_object('code','PLAN_SCENARIO_COVERAGE_INCOMPLETE','status',409,
   'errors',jsonb_build_object('workingHours',to_jsonb(missing))); END IF;
 END IF;
 SELECT * INTO rr FROM revision_reviews WHERE id=r.confirmation_review_id AND revision_id=v.revision_id AND scenario_version_id=v.id AND action='ConfirmForTraining' AND validation_run_id=pin.validation_run_id;
 SELECT run.* INTO vr FROM validation_runs run JOIN processing_jobs j ON j.id=run.job_id AND j.current_attempt_id=run.processing_attempt_id
  JOIN processing_job_attempts pa ON pa.id=run.processing_attempt_id AND pa.processing_job_id=j.id AND pa.status='Succeeded'
  WHERE run.id=pin.validation_run_id AND run.processing_attempt_id=pin.attempt_id AND run.scenario_version_id=v.id AND run.revision_id=v.revision_id AND run.scenario_hash=v.scenario_hash AND j.status='Succeeded' AND j.kind='ReleasePackage';
 SELECT * INTO p FROM release_packages WHERE release_id=r.id;
 SELECT * INTO a FROM revision_artifacts WHERE id=p.candidate_artifact_id AND id=vr.candidate_artifact_id AND attempt_id=pin.attempt_id AND job_id=vr.job_id AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready;
 SELECT * INTO m FROM revision_artifacts WHERE id=pin.manifest_artifact_id AND attempt_id=a.attempt_id AND job_id=a.job_id AND revision_id=a.revision_id AND artifact_type='manifest' AND is_runtime_ready;
 IF v.id IS NULL OR rr.id IS NULL OR vr.id IS NULL OR rr.annotation_set_id IS DISTINCT FROM vr.annotation_set_id THEN RETURN jsonb_build_object('code','RELEASE_READINESS_REQUIRED','status',409); END IF;
 IF vr.outcome IS DISTINCT FROM 'Passed' THEN RETURN jsonb_build_object('code','RELEASE_QA_NOT_PASSED','status',409); END IF;
 IF EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=vr.id AND severity IN('Error','Critical')) THEN RETURN jsonb_build_object('code','RELEASE_QA_BLOCKERS_PRESENT','status',409); END IF;
 IF a.id IS NULL OR m.id IS NULL THEN RETURN jsonb_build_object('code','RELEASE_ARTIFACT_REQUIRED','status',409); END IF;
 IF p.manifest_url IS DISTINCT FROM m.object_key OR p.manifest_sha256 IS DISTINCT FROM m.sha256_hash OR p.package_url IS DISTINCT FROM a.object_key OR p.checksum_sha256 IS DISTINCT FROM a.sha256_hash OR p.package_size_bytes IS DISTINCT FROM a.size_bytes
  OR p.min_runtime_version IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR p.schema_version IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR p.build_target IS DISTINCT FROM a.metadata->>'buildTarget'
  OR m.metadata->>'protocolVersion' IS DISTINCT FROM a.metadata->>'protocolVersion' OR m.metadata->>'manifestSchemaVersion' IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR m.metadata->>'buildTarget' IS DISTINCT FROM a.metadata->>'buildTarget' OR m.metadata->>'minRuntimeVersion' IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR m.metadata->'requiredCapabilities' IS DISTINCT FROM a.metadata->'requiredCapabilities' OR package_runtime_compatible(a.metadata,p.min_runtime_version) IS NOT TRUE
  THEN RETURN jsonb_build_object('code','PACKAGE_COMPATIBILITY_REQUIRED','status',409); END IF;
 UPDATE releases SET status='Published',published_at=stamp,published_by=p_actor,updated_at=stamp WHERE id=r.id;
 result:=release_representation(r.id);
 INSERT INTO release_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Publish',k,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Publish','Release',r.id,gen_random_uuid(),jsonb_build_object('status','Built'),jsonb_build_object('status','Published','publishedAt',stamp,'publishedBy',p_actor),stamp);
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

CREATE FUNCTION plan_dossier_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;b buildings;f fire_infrastructures;loc building_locations;contact building_contacts;scenario_list jsonb;
BEGIN
 IF p_action<>'Get' THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role::text NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO b FROM buildings WHERE id=p_resource AND deleted_at IS NULL;
 IF b.id IS NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
  OR (actor.role::text='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- The plan is built on one Approved fire infrastructure: input infrastructureId, else the newest Approved version of the Building.
 IF p_input->>'infrastructureId' IS NOT NULL THEN
  SELECT * INTO f FROM fire_infrastructures WHERE id=(p_input->>'infrastructureId')::uuid AND building_id=b.id AND status='Approved';
  IF f.id IS NULL THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_APPROVED','status',422,
   'errors',jsonb_build_object('infrastructureId',jsonb_build_array('Use an Approved fire infrastructure version of this Building.')));END IF;
 ELSE
  SELECT * INTO f FROM fire_infrastructures WHERE building_id=b.id AND status='Approved' ORDER BY decided_at DESC,version_number DESC LIMIT 1;
 END IF;
 SELECT * INTO loc FROM building_locations WHERE building_id=b.id;
 SELECT * INTO contact FROM building_contacts WHERE building_id=b.id ORDER BY is_primary DESC,created_at LIMIT 1;
 -- IV.1: the newest version of every scenario on that infrastructure, numbered 1, 2, 3... in creation order.
 SELECT COALESCE(jsonb_agg(jsonb_build_object('number',x.n,'scenarioId',x.scenario_id,'scenarioName',x.scenario_name,'scenarioVersionId',x.id,
   'versionNumber',x.version_number,'audience',x.state_snapshot->>'audience','assumption',x.state_snapshot->'assumption',
   'workingHours',x.state_snapshot->>'workingHours','reviewStatus',COALESCE(x.review_status,'NotSubmitted'),'reviewedAt',x.reviewed_at) ORDER BY x.n),'[]'::jsonb)
 INTO scenario_list
 FROM (SELECT row_number() OVER(ORDER BY s.created_at,s.id) n,s.id scenario_id,s.name scenario_name,v.id,v.version_number,v.state_snapshot,
   r.status review_status,r.reviewed_at
  FROM scenarios s
  JOIN LATERAL(SELECT * FROM scenario_versions sv WHERE sv.scenario_id=s.id AND sv.infrastructure_id=f.id ORDER BY sv.version_number DESC LIMIT 1) v ON true
  LEFT JOIN scenario_content_reviews r ON r.scenario_version_id=v.id
  WHERE s.building_id=b.id AND f.id IS NOT NULL) x;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object(
  'buildingId',b.id,'generatedAt',clock_timestamp(),'signedPlanDocumentId',b.signed_plan_document_id,
  'header',jsonb_build_object('name',b.name,
   'address',NULLIF(concat_ws(', ',NULLIF(btrim(loc.address),''),NULLIF(btrim(loc.district),''),NULLIF(btrim(loc.city),'')),''),
   'phone',COALESCE(contact.phone,b.facility_head_phone),'year',extract(year FROM clock_timestamp())::int),
  'siteLayout',CASE WHEN f.id IS NULL THEN NULL ELSE jsonb_build_object('revisionId',f.revision_id,'infrastructureId',f.id,'infrastructureVersion',f.version_number,
   'exits',(SELECT COALESCE(jsonb_agg(e.i ORDER BY e.n),'[]'::jsonb) FROM jsonb_array_elements(f.items) WITH ORDINALITY e(i,n) WHERE e.i->>'category'='Exit'),
   'assemblyPoints',(SELECT COALESCE(jsonb_agg(e.i ORDER BY e.n),'[]'::jsonb) FROM jsonb_array_elements(f.items) WITH ORDINALITY e(i,n) WHERE e.i->>'category'='AssemblyPoint')) END,
  'facility',jsonb_build_object('name',b.name,'facilityHeadName',b.facility_head_name,'facilityHeadPhone',b.facility_head_phone,'onSiteForce',b.on_site_force,
   'emergencyContacts',COALESCE(b.emergency_contacts,'[]'::jsonb),
   'equipment',(SELECT COALESCE(jsonb_agg(jsonb_build_object('catalogKey',q.k,'count',q.c) ORDER BY q.k),'[]'::jsonb) FROM (
     SELECT e.i->'capability'->>'id' k,count(*)::int c FROM jsonb_array_elements(COALESCE(f.items,'[]'::jsonb)) e(i) WHERE e.i->>'category'='Equipment' GROUP BY 1) q),
   'waterSources',(SELECT COALESCE(jsonb_agg(e.i),'[]'::jsonb) FROM jsonb_array_elements(COALESCE(f.items,'[]'::jsonb)) e(i) WHERE e.i->>'category'='WaterSource')),
  'hazard',jsonb_build_object('buildingType',b.building_type,'totalFloors',b.total_floors,'floorAreaSquareMeters',b.floor_area_square_meters),
  'scenarios',scenario_list,
  -- Same rule as publish_release_gate: any Approved version (matching hash) on this infrastructure, not only each scenario's newest one.
  'coverage',jsonb_build_object(
   'inHours',EXISTS(SELECT 1 FROM scenario_versions sv JOIN scenario_content_reviews scr ON scr.scenario_version_id=sv.id AND scr.status='Approved' AND scr.content_hash=sv.scenario_hash
     WHERE sv.infrastructure_id=f.id AND sv.state_snapshot->>'workingHours'='InHours'),
   'offHours',EXISTS(SELECT 1 FROM scenario_versions sv JOIN scenario_content_reviews scr ON scr.scenario_version_id=sv.id AND scr.status='Approved' AND scr.content_hash=sv.scenario_hash
     WHERE sv.infrastructure_id=f.id AND sv.state_snapshot->>'workingHours'='OffHours')),
  'history',(SELECT COALESCE(jsonb_agg(jsonb_build_object('id',h.id,'version',h.version_number,'status',h.status,'submittedAt',h.submitted_at,'decidedAt',h.decided_at)
    ORDER BY h.version_number DESC),'[]'::jsonb) FROM fire_infrastructures h WHERE h.revision_id=f.revision_id)));
END $$;

DO $grants$ DECLARE s record;r text;t text;BEGIN
 FOREACH t IN ARRAY ARRAY['building_locations','building_contacts','scenarios','scenario_versions','scenario_content_reviews','fire_infrastructures','buildings','organizations','users'] LOOP
  EXECUTE format('GRANT SELECT ON %I TO fet3d_ifc_upload_owner',t);
  IF (SELECT relrowsecurity FROM pg_class WHERE oid=format('public.%I',t)::regclass) AND NOT EXISTS(SELECT 1 FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=t
     AND ('fet3d_ifc_upload_owner'=ANY(p.roles) OR 'public'=ANY(p.roles)) AND p.cmd IN('ALL','SELECT')) THEN
   EXECUTE format('CREATE POLICY plan_dossier_owner_read ON %I FOR SELECT TO fet3d_ifc_upload_owner USING(true)',t);
  END IF;
 END LOOP;
 ALTER FUNCTION plan_dossier_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION plan_dossier_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('REVOKE ALL ON FUNCTION plan_dossier_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION plan_dossier_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM plan_dossier_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
