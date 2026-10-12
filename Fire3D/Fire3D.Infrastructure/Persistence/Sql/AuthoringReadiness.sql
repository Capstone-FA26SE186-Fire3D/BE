-- Read-only technical readiness for a revision and for a scenario version (#53), so a client can resume work from the
-- Building/scenario after a reload. The version read does not need a submitted content review. Nothing here changes the
-- confirm/publish gates: readiness is reported, never granted.
CREATE TEMP TABLE authoring_readiness_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO authoring_readiness_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;
GRANT SELECT ON processing_jobs,processing_job_attempts,validation_runs,revision_issues,revision_reviews,revision_artifacts TO fet3d_ifc_upload_owner;

CREATE FUNCTION authoring_readiness_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;v scenario_versions;rv revisions;run validation_runs;job processing_jobs;
 blockers bigint;confirmation uuid;ready boolean;missing text[]:='{}';
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
  ready:=COALESCE(run.outcome='Passed' AND blockers=0 AND confirmation IS NOT NULL
   AND EXISTS(SELECT 1 FROM revision_artifacts WHERE id=run.candidate_artifact_id AND job_id=run.job_id AND attempt_id=run.processing_attempt_id
    AND revision_id=v.revision_id AND artifact_type='unity_package' AND is_runtime_ready),false);
  IF run.id IS NULL THEN missing:=array_append(missing,'PACKAGE_VALIDATION_REQUIRED');
  ELSE
   IF run.outcome<>'Passed' THEN missing:=array_append(missing,'QA_NOT_PASSED');END IF;
   IF blockers>0 THEN missing:=array_append(missing,'QA_BLOCKERS_PRESENT');END IF;
   IF confirmation IS NULL THEN missing:=array_append(missing,'CONFIRMATION_REQUIRED');END IF;
   IF run.outcome='Passed' AND blockers=0 AND confirmation IS NOT NULL AND NOT ready THEN missing:=array_append(missing,'RUNTIME_ARTIFACT_REQUIRED');END IF;
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
  'confirmations',COALESCE((SELECT jsonb_agg(jsonb_build_object('reviewId',x.id,'scenarioVersionId',x.scenario_version_id,'validationRunId',x.validation_run_id,'reviewedAt',x.reviewed_at) ORDER BY x.reviewed_at DESC,x.id DESC)
    FROM (SELECT * FROM revision_reviews WHERE revision_id=rv.id AND action='ConfirmForTraining' ORDER BY reviewed_at DESC,id DESC LIMIT 50) x),'[]')));
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION authoring_readiness_gate(text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION authoring_readiness_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION authoring_readiness_gate(text,uuid,uuid,uuid,jsonb,text,bigint) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION authoring_readiness_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM authoring_readiness_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
