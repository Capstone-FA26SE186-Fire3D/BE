DO $migration$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;r text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 EXECUTE $definition$
-- Private projections. API identities can execute only the actor/session-aware read gate.
CREATE FUNCTION scenario_review_summary(p_review uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('reviewId',r.id,'scenarioVersionId',v.id,'status',r.status,
  'contentHash',r.content_hash,'rubricHash',r.rubric_hash,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,
  'decidedBy',r.reviewed_by,'decidedAt',r.reviewed_at,'reason',r.reason,'organizationId',o.id,'organizationName',o.name,
  'buildingId',b.id,'buildingName',b.name,'scenarioId',s.id,'scenarioName',s.name,'versionNumber',v.version_number)
 FROM scenario_content_reviews r JOIN scenario_versions v ON v.id=r.scenario_version_id
 JOIN scenarios s ON s.id=v.scenario_id JOIN buildings b ON b.id=v.building_id JOIN organizations o ON o.id=b.organization_id
 WHERE r.id=p_review;
$$;

CREATE FUNCTION scenario_review_read_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users; r scenario_content_reviews; v scenario_versions; b buildings; run validation_runs;
 confirmation uuid; blockers bigint; total integer; result jsonb; requested_status text;
 org uuid; page_number integer; page_size integer;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL
  AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family
  AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp())
  THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('PlatformAdmin','OrganizationUser') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 IF actor.role='OrganizationUser' AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=actor.organization_id AND is_active AND deleted_at IS NULL)
  THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF p_action='List' THEN
  IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
  requested_status:=p_input->>'status'; org:=(p_input->>'organizationId')::uuid;
  page_number:=coalesce((p_input->>'page')::integer,1); page_size:=coalesce((p_input->>'pageSize')::integer,20);
  IF (requested_status IS NOT NULL AND requested_status NOT IN('Submitted','Approved','Rejected')) OR page_number<1 OR page_size NOT BETWEEN 1 AND 100
   THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400); END IF;
  SELECT count(*) INTO total FROM scenario_content_reviews review JOIN scenario_versions version ON version.id=review.scenario_version_id
   WHERE (requested_status IS NULL OR review.status=requested_status) AND (org IS NULL OR version.organization_id=org);
  SELECT coalesce(jsonb_agg(scenario_review_summary(q.id) ORDER BY q.submitted_at DESC,q.id DESC),'[]'::jsonb) INTO result
   FROM (SELECT review.id,review.submitted_at FROM scenario_content_reviews review JOIN scenario_versions version ON version.id=review.scenario_version_id
    WHERE (requested_status IS NULL OR review.status=requested_status) AND (org IS NULL OR version.organization_id=org)
    ORDER BY review.submitted_at DESC,review.id DESC LIMIT page_size OFFSET (page_number::bigint-1)*page_size) q;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size));
 END IF;
 IF p_action='States' THEN
  IF jsonb_typeof(p_input->'ids') IS DISTINCT FROM 'array' OR jsonb_array_length(p_input->'ids')>100
   THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400); END IF;
  SELECT coalesce(jsonb_agg(jsonb_build_object('scenarioVersionId',version.id,'reviewStatus',coalesce(review.status,'NotSubmitted'),
   'reviewId',review.id,'rejectReason',CASE WHEN review.status='Rejected' THEN review.reason END) ORDER BY version.id),'[]'::jsonb) INTO result
   FROM scenario_versions version JOIN buildings building ON building.id=version.building_id
   LEFT JOIN scenario_content_reviews review ON review.scenario_version_id=version.id
   WHERE version.id IN(SELECT value::uuid FROM jsonb_array_elements_text(p_input->'ids'))
    AND (actor.role='PlatformAdmin' OR (version.organization_id=actor.organization_id AND building.organization_id=actor.organization_id AND building.is_active AND building.deleted_at IS NULL));
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;
 IF p_action='Detail' THEN SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource;
 ELSIF p_action='Version' THEN SELECT * INTO r FROM scenario_content_reviews WHERE scenario_version_id=p_resource;
 ELSE RETURN jsonb_build_object('code','ACTION_INVALID','status',400); END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id;
 SELECT * INTO b FROM buildings WHERE id=v.building_id;
 IF r.id IS NULL OR b.id IS NULL OR (actor.role='OrganizationUser' AND (v.organization_id IS DISTINCT FROM actor.organization_id OR b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL))
  THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 SELECT vr.* INTO run FROM validation_runs vr JOIN processing_jobs j ON j.id=vr.job_id AND j.current_attempt_id=vr.processing_attempt_id
  JOIN processing_job_attempts a ON a.id=vr.processing_attempt_id AND a.processing_job_id=j.id AND a.status='Succeeded'
  WHERE vr.scenario_version_id=v.id AND vr.revision_id=v.revision_id AND vr.scenario_hash=v.scenario_hash AND j.kind='ReleasePackage' AND j.status='Succeeded'
  ORDER BY vr.created_at DESC,vr.id DESC LIMIT 1;
 SELECT count(*) INTO blockers FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical');
 SELECT id INTO confirmation FROM revision_reviews WHERE scenario_version_id=v.id AND revision_id=v.revision_id AND validation_run_id=run.id
  AND annotation_set_id IS NOT DISTINCT FROM run.annotation_set_id AND action='ConfirmForTraining' ORDER BY reviewed_at DESC,id DESC LIMIT 1;
 result:=jsonb_build_object('review',scenario_review_summary(r.id),'content',v.state_snapshot,'rubric',v.rubric,
  'learningObjectives',v.learning_objectives,'learnerInstructions',v.learner_instructions,
  'readiness',jsonb_build_object('validationRunId',run.id,'outcome',run.outcome,'blockerCount',blockers,'confirmationReviewId',confirmation,
   'candidateArtifactId',run.candidate_artifact_id,'technicalReady',coalesce(run.outcome='Passed' AND blockers=0 AND confirmation IS NOT NULL
    AND EXISTS(SELECT 1 FROM revision_artifacts WHERE id=run.candidate_artifact_id AND job_id=run.job_id AND attempt_id=run.processing_attempt_id
     AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready),false)));
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

$definition$;
 ALTER FUNCTION scenario_review_summary(uuid) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION scenario_review_summary(uuid),scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r); END IF;
 END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF changed THEN
  IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $migration$;
