-- Fixes for IFC-03/IFC-04 found in review (forward migration: 20261015091000/20261015092000 are already applied).
-- 1. bim_facts upsert takes one row per space GlobalId, so a building_metadata with a repeated space no longer fails Complete
--    (ON CONFLICT cannot touch a row twice) and loops the job; the API also rejects repeated space GlobalIds at /outputs.
-- 2. revision_equipment_gate reads interactables only when it is an array (an explicit JSON null made GET/PATCH fail).
-- 3. Revisions whose current Geometry run had already failed before 20261015092000 move to NeedsFix like new ones.
CREATE TEMP TABLE ifc_review_fix_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ifc_review_fix_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION processing_worker_gate_before_streams(p_action text,p_job uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE j processing_jobs;a processing_job_attempts;e integration_outbox_events;receipt processing_delivery_receipts;
 b buildings;aid uuid;token uuid;rid uuid;outid uuid;v uuid;outrow record;summary jsonb;h text;k text;issue jsonb;primary_art uuid;
BEGIN
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO j FROM processing_jobs WHERE id=p_job;
 SELECT b0.* INTO b FROM revisions r JOIN buildings b0 ON b0.id=r.building_id WHERE r.id=j.revision_id;
 IF j.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','JOB_NOT_FOUND','status',404); END IF;
 IF j.kind NOT IN('Geometry','PlaytestPackage','ReleasePackage') OR NOT EXISTS(SELECT 1 FROM source_documents s WHERE s.id=j.source_document_id AND s.revision_id=j.revision_id AND s.upload_verified_at IS NOT NULL AND s.source_etag IS NOT NULL)
 THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','JOB_SCOPE_UNAVAILABLE','status',409); END IF;
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=p_input->>'eventKey' FOR UPDATE;
  IF e.aggregate_id IS DISTINCT FROM j.id OR e.payload_hash IS DISTINCT FROM p_input->>'payloadHash' OR e.schema_version<>'1' OR e.event_type NOT IN('ProcessingJobRequested','ProcessingJobRequeue') THEN RETURN jsonb_build_object('code','DELIVERY_CONFLICT','status',409); END IF;
 END IF;
 SELECT * INTO j FROM processing_jobs WHERE id=p_job FOR UPDATE;
 IF p_action='Claim' THEN
  SELECT * INTO receipt FROM processing_delivery_receipts WHERE event_key=e.idempotency_key;
  IF receipt.attempt_id IS NOT NULL THEN
   SELECT * INTO a FROM processing_job_attempts WHERE id=receipt.attempt_id;
   IF receipt.payload_hash<>e.payload_hash THEN RETURN jsonb_build_object('code','DELIVERY_CONFLICT','status',409); END IF;
   IF a.id IS DISTINCT FROM j.current_attempt_id OR a.status='Expired' THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
   RETURN jsonb_build_object('code','OK','replayed',true,'status',a.status,'attemptId',a.id,'leaseToken',a.lease_token,'leaseUntil',a.lease_until,'receiptId',receipt.receipt_id,'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/','inputHash',j.input_hash,'sourceKey',(SELECT storage_url FROM source_documents WHERE id=j.source_document_id),'sourceHash',(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),'scenarioVersionId',j.scenario_version_id);
  END IF;
  IF j.status NOT IN('Queued','Running') THEN RETURN jsonb_build_object('code','JOB_NOT_CLAIMABLE','status',409); END IF;
  IF p_input->>'inputHash' IS DISTINCT FROM j.input_hash OR NULLIF(p_input->>'toolchainVersion','') IS NULL THEN RETURN jsonb_build_object('code','INPUT_PROVENANCE_CONFLICT','status',409); END IF;
  SELECT * INTO a FROM processing_job_attempts WHERE id=j.current_attempt_id;
  IF a.status='Running' AND a.lease_until>clock_timestamp() THEN RETURN jsonb_build_object('code','JOB_BUSY','status',409); END IF;
  IF a.status='Running' THEN UPDATE processing_job_attempts SET status='Expired',finished_at=clock_timestamp() WHERE id=a.id; END IF;
  aid:=gen_random_uuid();token:=gen_random_uuid();rid:=gen_random_uuid();
  INSERT INTO processing_job_attempts(id,processing_job_id,attempt_number,input_hash,toolchain_version,lease_owner,lease_token,lease_until,status)
   SELECT aid,j.id,COALESCE(max(attempt_number),0)+1,j.input_hash,p_input->>'toolchainVersion','http-worker',token,clock_timestamp()+interval '60 seconds','Running' FROM processing_job_attempts WHERE processing_job_id=j.id;
  UPDATE processing_jobs SET current_attempt_id=aid,status='Running',started_at=COALESCE(started_at,clock_timestamp()),heartbeat_at=clock_timestamp() WHERE id=j.id;
  INSERT INTO processing_delivery_receipts(event_key,payload_hash,attempt_id,receipt_id) VALUES(e.idempotency_key,e.payload_hash,aid,rid);
  RETURN jsonb_build_object('code','OK','replayed',false,'status','Running','attemptId',aid,'leaseToken',token,'leaseUntil',clock_timestamp()+interval '60 seconds','receiptId',rid,'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||aid||'/','inputHash',j.input_hash,'sourceKey',(SELECT storage_url FROM source_documents WHERE id=j.source_document_id),'sourceHash',(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),'scenarioVersionId',j.scenario_version_id);
 END IF;
 SELECT * INTO a FROM processing_job_attempts WHERE id=(p_input->>'attemptId')::uuid AND processing_job_id=j.id FOR UPDATE;
 IF a.id IS NULL OR a.id IS DISTINCT FROM j.current_attempt_id OR a.lease_token IS DISTINCT FROM (p_input->>'leaseToken')::uuid THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
 IF p_action='Complete' AND a.status='Succeeded' THEN
  IF a.output_hash=p_input->>'outputHash' THEN RETURN jsonb_build_object('code','OK','outputHash',a.output_hash); END IF;
  RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409);
 END IF;
 IF a.status<>'Running' OR a.lease_until<=clock_timestamp() THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
 IF p_action='Renew' THEN
  UPDATE processing_job_attempts SET lease_until=clock_timestamp()+interval '60 seconds' WHERE id=a.id;
  UPDATE processing_jobs SET heartbeat_at=clock_timestamp() WHERE id=j.id;
  RETURN jsonb_build_object('code','OK','leaseUntil',clock_timestamp()+interval '60 seconds');
 ELSIF p_action='Output' THEN
  summary:=p_input->'output';h:=fet3d_jsonb_payload_hash(summary);k:='worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/';
  IF summary->>'objectKey' NOT LIKE k||'%' OR summary->>'objectKey' ~ '(^|/)\.\.(/|$)' OR summary->>'sha256Hash' !~ '^[0-9a-f]{64}$' OR (summary->>'sizeBytes')::bigint<=0
   OR summary->>'outcome' NOT IN('Passed','Failed') OR NULLIF(summary->>'validatorVersion','') IS NULL OR jsonb_typeof(summary->'metadata')<>'object' OR jsonb_typeof(summary->'issues')<>'array'
   OR NULLIF(summary->>'artifactType','') IS NULL OR NULLIF(summary->>'schemaVersion','') IS NULL
  THEN RETURN jsonb_build_object('code','OUTPUT_INVALID','status',400); END IF;
  FOR issue IN SELECT value FROM jsonb_array_elements(summary->'issues') LOOP
   IF issue->>'severity' NOT IN('Info','Warning','Error','Critical') OR NULLIF(issue->>'code','') IS NULL OR NULLIF(issue->>'message','') IS NULL THEN RETURN jsonb_build_object('code','OUTPUT_INVALID','status',400); END IF;
  END LOOP;
  SELECT artifact_id INTO outid FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type=summary->>'artifactType';
  IF outid IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type=summary->>'artifactType' AND input_hash=h) THEN RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409); END IF;
  ELSE
   outid:=gen_random_uuid(); INSERT INTO processing_candidate_outputs(attempt_id,artifact_type,input_hash,artifact_id,output) VALUES(a.id,summary->>'artifactType',h,outid,summary);
  END IF;
  SELECT fet3d_jsonb_payload_hash(jsonb_agg(output ORDER BY artifact_type)) INTO h FROM processing_candidate_outputs WHERE attempt_id=a.id;
  RETURN jsonb_build_object('code','OK','artifactId',outid,'outputHash',h);
 ELSIF p_action='Complete' THEN
  SELECT fet3d_jsonb_payload_hash(jsonb_agg(output ORDER BY artifact_type)) INTO h FROM processing_candidate_outputs WHERE attempt_id=a.id;
  IF h IS NULL OR h IS DISTINCT FROM p_input->>'outputHash' THEN RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409); END IF;
  IF j.kind IN('PlaytestPackage','ReleasePackage') AND (NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type='unity_package') OR NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type='manifest')) THEN RETURN jsonb_build_object('code','PACKAGE_OUTPUT_INCOMPLETE','status',409); END IF;
  v:=gen_random_uuid();
  FOR outrow IN SELECT * FROM processing_candidate_outputs WHERE attempt_id=a.id ORDER BY artifact_type LOOP
   INSERT INTO revision_artifacts(id,revision_id,job_id,artifact_type,object_key,sha256_hash,size_bytes,schema_version,metadata,created_at,attempt_id,is_runtime_ready)
    VALUES(outrow.artifact_id,j.revision_id,j.id,outrow.artifact_type,outrow.output->>'objectKey',outrow.output->>'sha256Hash',(outrow.output->>'sizeBytes')::bigint,outrow.output->>'schemaVersion',outrow.output->'metadata',now(),a.id,j.kind IN('PlaytestPackage','ReleasePackage') AND outrow.output->>'outcome'='Passed');
   IF primary_art IS NULL OR outrow.artifact_type IN('unity_package','preview_glb') THEN primary_art:=outrow.artifact_id; summary:=outrow.output; END IF;
  END LOOP;
  INSERT INTO validation_runs(id,revision_id,job_id,kind,scenario_version_id,candidate_artifact_id,outcome,scenario_hash,validator_version,report,created_at,processing_attempt_id,started_at,finished_at)
   VALUES(v,j.revision_id,j.id,j.kind,j.scenario_version_id,primary_art,CASE WHEN EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND (output->>'outcome'='Failed' OR EXISTS(SELECT 1 FROM jsonb_array_elements(output->'issues') i WHERE i->>'severity' IN('Error','Critical')))) THEN 'Failed' ELSE 'Passed' END,
    CASE WHEN j.scenario_version_id IS NULL THEN NULL ELSE (SELECT scenario_hash FROM scenario_versions WHERE id=j.scenario_version_id) END,summary->>'validatorVersion',jsonb_build_object('outputHash',h,'toolchainVersion',a.toolchain_version),now(),a.id,a.started_at,clock_timestamp());
  FOR issue IN SELECT value FROM processing_candidate_outputs o CROSS JOIN LATERAL jsonb_array_elements(o.output->'issues') WHERE o.attempt_id=a.id LOOP
   INSERT INTO revision_issues(id,validation_run_id,severity,code,ifc_guid,message,details,created_at) VALUES(gen_random_uuid(),v,issue->>'severity',issue->>'code',
    CASE WHEN jsonb_typeof(issue->'ifcGlobalIds')='array' THEN issue->'ifcGlobalIds'->>0 END,issue->>'message',
    COALESCE(issue->'details','{}'::jsonb)||CASE WHEN jsonb_typeof(issue->'ifcGlobalIds')='array' THEN jsonb_build_object('ifcGlobalIds',issue->'ifcGlobalIds') ELSE '{}'::jsonb END,now());
  END LOOP;
  UPDATE processing_job_attempts SET status='Succeeded',output_hash=h,finished_at=clock_timestamp() WHERE id=a.id;
  UPDATE processing_jobs SET status='Succeeded',finished_at=clock_timestamp(),error_message=NULL WHERE id=j.id;
  IF j.kind='Geometry' THEN
   UPDATE revisions SET status='ReadyForScenario',updated_at=now() WHERE id=j.revision_id AND EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Passed');
   UPDATE revisions SET status='NeedsFix',updated_at=now() WHERE id=j.revision_id AND EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Failed')
    AND status NOT IN('ConfirmedForTraining','Superseded','Rejected');
   -- Room facts from building_metadata.spaces of an accepted run: Name, LongName (Revit keeps the room name there) and Category.
   IF EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Passed') THEN
    INSERT INTO bim_facts(id,revision_id,ifc_global_id,entity_type,property_path,value,source_hash,quality_flags,created_at)
    SELECT gen_random_uuid(),j.revision_id,d.sp->>'ifcGlobalId','IfcSpace',p.path,p.val,(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),
     CASE WHEN p.path='Name' AND NULLIF(btrim(COALESCE(d.sp->>'name','')),'') IS NULL AND NULLIF(btrim(COALESCE(d.sp->>'longName','')),'') IS NULL
      THEN '["SPACE_UNNAMED"]'::jsonb ELSE '[]'::jsonb END,now()
    -- One row per GlobalId (first occurrence): a repeated space must never make the upsert touch a row twice and fail Complete.
    FROM (SELECT DISTINCT ON (s->>'ifcGlobalId') s AS sp
      FROM processing_candidate_outputs o CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(o.output->'metadata'->'spaces')='array' THEN o.output->'metadata'->'spaces' ELSE '[]' END) WITH ORDINALITY e(s,ord)
      WHERE o.attempt_id=a.id AND o.artifact_type='building_metadata' AND jsonb_typeof(s)='object' AND s->>'ifcGlobalId' ~ '^[0-9A-Za-z_$]{22}$'
      ORDER BY s->>'ifcGlobalId',o.artifact_id,ord) d
     CROSS JOIN LATERAL (VALUES('Name',d.sp->'name'),('LongName',d.sp->'longName'),('Category',d.sp->'category')) p(path,val)
    ON CONFLICT(revision_id,ifc_global_id,property_path) DO UPDATE SET entity_type=EXCLUDED.entity_type,value=EXCLUDED.value,source_hash=EXCLUDED.source_hash,
     quality_flags=EXCLUDED.quality_flags,created_at=EXCLUDED.created_at;
   END IF;
  END IF;
  INSERT INTO audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),b.organization_id,'System','Update','ProcessingJob',j.id,gen_random_uuid(),jsonb_build_object('attemptId',a.id,'outputHash',h),now());
  RETURN jsonb_build_object('code','OK','validationRunId',v,'outputHash',h);
 ELSIF p_action='Fail' THEN
  UPDATE processing_job_attempts SET status='Failed',finished_at=clock_timestamp(),error_message=left(p_input->>'reason',1000) WHERE id=a.id;
  UPDATE processing_jobs SET status='Failed',finished_at=clock_timestamp(),error_message=left(p_input->>'reason',1000) WHERE id=j.id;
  INSERT INTO audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),b.organization_id,'System','Update','ProcessingJob',j.id,gen_random_uuid(),jsonb_build_object('attemptId',a.id,'status','Failed'),now());
  RETURN jsonb_build_object('code','OK');
 END IF;
 RETURN jsonb_build_object('code','WORKER_ACTION_INVALID','status',400);
END $$;

CREATE OR REPLACE FUNCTION revision_equipment_gate(p_action text,p_actor uuid,p_family uuid,p_revision uuid,p_input jsonb,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;rev revisions;geo revision_artifacts;meta revision_artifacts;cur integer;decisions jsonb;prev_items jsonb;prev_rev uuid;
 item jsonb;cand jsonb;issues jsonb:='[]';n integer:=0;eff text;normalized jsonb:='[]';stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Get','Save') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT r.* INTO rev FROM revisions r JOIN buildings b ON b.id=r.building_id JOIN organizations o ON o.id=b.organization_id
  WHERE r.id=p_revision AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
   AND (actor.role='PlatformAdmin' OR b.organization_id=actor.organization_id);
 IF rev.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='Save' THEN PERFORM 1 FROM revisions WHERE id=rev.id FOR UPDATE;END IF;
 -- The accepted Geometry attempt: current succeeded attempt, Passed QA, no Error/Critical issue (the editor's rule).
 SELECT a.* INTO geo FROM revision_artifacts a
  JOIN processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
  JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND pa.processing_job_id=j.id AND j.current_attempt_id=pa.id
  WHERE a.revision_id=rev.id AND a.artifact_type='preview_glb' AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded'
  AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=j.id AND vr.processing_attempt_id=pa.id AND vr.revision_id=a.revision_id
   AND vr.kind='Geometry' AND vr.outcome='Passed' AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')))
  ORDER BY a.created_at DESC,a.id LIMIT 1;
 IF geo.id IS NULL THEN RETURN jsonb_build_object('code','EQUIPMENT_CANDIDATES_UNAVAILABLE','status',409);END IF;
 SELECT * INTO meta FROM revision_artifacts WHERE revision_id=rev.id AND attempt_id=geo.attempt_id AND artifact_type='building_metadata' ORDER BY created_at DESC,id LIMIT 1;
 SELECT COALESCE(max(version_number),0) INTO cur FROM revision_equipment_decisions WHERE revision_id=rev.id;
 SELECT COALESCE((SELECT data->'items' FROM revision_equipment_decisions WHERE revision_id=rev.id ORDER BY version_number DESC LIMIT 1),'[]') INTO decisions;

 IF p_action='Save' THEN
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  IF p_expected<>cur THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF meta.id IS NULL THEN RETURN jsonb_build_object('code','EQUIPMENT_CANDIDATES_UNAVAILABLE','status',409);END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(COALESCE(p_input->'items','[]')) LOOP
   SELECT c INTO cand FROM jsonb_array_elements(CASE WHEN jsonb_typeof(meta.metadata->'interactables')='array' THEN meta.metadata->'interactables' ELSE '[]'::jsonb END) c WHERE c->>'id'=item->>'candidateId' LIMIT 1;
   IF cand IS NULL THEN
    issues:=issues||jsonb_build_array(jsonb_build_object('code','CANDIDATE_NOT_FOUND','path','$.items['||n||'].candidateId','message','The candidate is not in the accepted geometry of this revision.'));
   ELSE
    eff:=CASE WHEN item->>'decision'='Rejected' THEN NULL ELSE COALESCE(NULLIF(item->>'catalogKey',''),cand->>'catalogKey') END;
    IF eff IS NOT NULL AND NOT EXISTS(SELECT 1 FROM runtime_compatibility_catalog rc WHERE rc.is_active AND rc.capabilities ? eff
      AND jsonb_typeof(rc.capability_contracts->eff->'objectKinds')='array' AND rc.capability_contracts->eff->'objectKinds' ? 'Equipment') THEN
     issues:=issues||jsonb_build_array(jsonb_build_object('code','CATALOG_KEY_UNSUPPORTED','path','$.items['||n||'].catalogKey','message','Use an Equipment capability of an active runtime.'));
    END IF;
    normalized:=normalized||jsonb_build_array(jsonb_build_object('candidateId',cand->>'id','ifcGlobalId',cand->>'ifcGlobalId','decision',item->>'decision',
     'catalogKey',eff,'note',NULLIF(item->>'note',''),'decidedBy',p_actor,'decidedAt',stamp));
   END IF;
   n:=n+1;
  END LOOP;
  IF n=0 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','EQUIPMENT_DECISION_INVALID','status',422,'issues',issues);END IF;
  decisions:=COALESCE((SELECT jsonb_agg(d) FROM jsonb_array_elements(decisions) d
   WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(normalized) x WHERE x->>'candidateId'=d->>'candidateId')),'[]')||normalized;
  cur:=cur+1;
  INSERT INTO revision_equipment_decisions(id,revision_id,version_number,data,created_by,created_at) VALUES(gen_random_uuid(),rev.id,cur,jsonb_build_object('items',decisions),p_actor,stamp);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,(SELECT b.organization_id FROM buildings b WHERE b.id=rev.building_id),'User','Update','RevisionEquipmentDecisions',rev.id,gen_random_uuid(),
   jsonb_build_object('version',cur,'changed',jsonb_array_length(normalized)),now());
 END IF;

 SELECT d.revision_id,d.data->'items' INTO prev_rev,prev_items FROM revision_equipment_decisions d JOIN revisions r2 ON r2.id=d.revision_id
  WHERE r2.building_id=rev.building_id AND r2.id<>rev.id AND r2.created_at<=rev.created_at ORDER BY r2.created_at DESC,d.version_number DESC LIMIT 1;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('revisionId',rev.id,'artifactId',meta.id,'metadataAvailable',meta.id IS NOT NULL,'version',cur,
  'items',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',c->>'id','ifcGlobalId',c->>'ifcGlobalId','catalogKey',c->>'catalogKey','source',c->>'source',
    'ifcClass',c->'ifcClass','confidence',(c->>'confidence')::numeric,'floorId',c->'floorId','position',c->'position','rotationY',c->'rotationY','bbox',c->'bbox',
    'needsConfirmation',(c->>'confidence')::numeric<0.9,
    'decision',(SELECT jsonb_build_object('decision',d->>'decision','catalogKey',d->'catalogKey','note',d->'note','decidedBy',d->'decidedBy','decidedAt',d->'decidedAt')
      FROM jsonb_array_elements(decisions) d WHERE d->>'candidateId'=c->>'id' LIMIT 1),
    'suggestedDecision',CASE WHEN NOT EXISTS(SELECT 1 FROM jsonb_array_elements(decisions) d WHERE d->>'candidateId'=c->>'id') THEN
      (SELECT jsonb_build_object('decision',d->>'decision','catalogKey',d->'catalogKey','fromRevisionId',prev_rev)
       FROM jsonb_array_elements(COALESCE(prev_items,'[]')) d WHERE d->>'ifcGlobalId'=c->>'ifcGlobalId' LIMIT 1) END) ORDER BY o)
   FROM jsonb_array_elements(CASE WHEN jsonb_typeof(meta.metadata->'interactables')='array' THEN meta.metadata->'interactables' ELSE '[]'::jsonb END) WITH ORDINALITY e(c,o)),'[]')));
END $$;

UPDATE revisions r SET status='NeedsFix',updated_at=now()
 WHERE r.status NOT IN('NeedsFix','ConfirmedForTraining','Superseded','Rejected')
  AND EXISTS(SELECT 1 FROM processing_jobs j JOIN validation_runs vr ON vr.job_id=j.id AND vr.processing_attempt_id=j.current_attempt_id AND vr.revision_id=j.revision_id
   WHERE j.revision_id=r.id AND j.kind='Geometry' AND j.status='Succeeded' AND vr.kind='Geometry' AND vr.outcome='Failed');

DO $grants$ DECLARE s record;BEGIN
 ALTER FUNCTION processing_worker_gate_before_streams(text,uuid,jsonb) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION revision_equipment_gate(text,uuid,uuid,uuid,jsonb,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION processing_worker_gate_before_streams(text,uuid,jsonb) FROM PUBLIC;
 REVOKE ALL ON FUNCTION revision_equipment_gate(text,uuid,uuid,uuid,jsonb,bigint) FROM PUBLIC;
 SELECT * INTO s FROM ifc_review_fix_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
