-- Functions are SECURITY INVOKER unless explicitly documented otherwise.
-- Browser/mobile never connect to DB. API authenticates actor; these checks do not authenticate UUID arguments.
CREATE FUNCTION assert_org_user(p_user UUID, p_org UUID) RETURNS VOID LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM users u JOIN organizations o ON o.id=u.organization_id
 WHERE u.id=p_user AND u.role='OrganizationUser' AND u.organization_id=p_org
 AND u.is_active AND u.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL)
 THEN RAISE EXCEPTION 'Active OrganizationUser and organization required'; END IF;
END $$;
CREATE FUNCTION lock_content(p_building UUID) RETURNS VOID LANGUAGE sql AS $$
 SELECT pg_advisory_xact_lock(hashtextextended(p_building::text,601));
$$;
CREATE FUNCTION deny_snapshot_mutation() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% is append-only; create a new version', TG_TABLE_NAME; END $$;

CREATE FUNCTION validate_owned_content() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE v_building UUID; v_org UUID; v_actor UUID;
BEGIN
 IF TG_TABLE_NAME='buildings' THEN
   v_building:=NEW.id; v_org:=NEW.organization_id; v_actor:=NEW.created_by;
 ELSE
   v_building:=NEW.building_id; v_org:=NEW.organization_id;
   v_actor:=COALESCE((to_jsonb(NEW)->>'uploaded_by')::uuid,(to_jsonb(NEW)->>'created_by')::uuid);
 END IF;
 PERFORM lock_content(v_building);
 IF TG_OP='INSERT' THEN PERFORM assert_org_user(v_actor,v_org);
 ELSIF (to_jsonb(NEW)->>'organization_id',to_jsonb(NEW)->>'building_id',to_jsonb(NEW)->>'created_by',to_jsonb(NEW)->>'uploaded_by')
 IS DISTINCT FROM (to_jsonb(OLD)->>'organization_id',to_jsonb(OLD)->>'building_id',to_jsonb(OLD)->>'created_by',to_jsonb(OLD)->>'uploaded_by')
 THEN RAISE EXCEPTION 'Ownership and creator are immutable'; END IF;
 IF TG_TABLE_NAME<>'buildings' AND TG_OP='INSERT' AND NOT EXISTS(SELECT 1 FROM buildings WHERE id=v_building AND is_active AND deleted_at IS NULL)
 THEN RAISE EXCEPTION 'Active building required'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER owned_building BEFORE INSERT OR UPDATE ON buildings FOR EACH ROW EXECUTE FUNCTION validate_owned_content();
CREATE TRIGGER owned_revision BEFORE INSERT OR UPDATE ON revisions FOR EACH ROW EXECUTE FUNCTION validate_owned_content();
CREATE TRIGGER owned_scenario BEFORE INSERT ON scenarios FOR EACH ROW EXECUTE FUNCTION validate_owned_content();

CREATE FUNCTION validate_source() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF r.status<>'Draft' THEN RAISE EXCEPTION 'Source can be registered/quarantined only while revision Draft'; END IF;
 IF TG_OP='INSERT' THEN PERFORM assert_org_user(NEW.uploaded_by,r.organization_id);
 ELSIF (to_jsonb(NEW)-ARRAY['quarantine_status','quarantine_note']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['quarantine_status','quarantine_note'])
 OR OLD.quarantine_status<>'Pending' THEN RAISE EXCEPTION 'Source identity immutable; only pending quarantine can be resolved'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER source_write BEFORE INSERT OR UPDATE ON source_documents FOR EACH ROW EXECUTE FUNCTION validate_source();
CREATE TRIGGER source_no_delete BEFORE DELETE ON source_documents FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation();

CREATE FUNCTION enforce_revision_status_transition() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 PERFORM lock_content(NEW.building_id);
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Draft' THEN RAISE EXCEPTION 'Revision starts Draft'; END IF;
   RETURN NEW;
 END IF;
 IF (NEW.id,NEW.building_id,NEW.organization_id,NEW.uploaded_by,NEW.version_label,NEW.primary_type,NEW.created_at)
 IS DISTINCT FROM (OLD.id,OLD.building_id,OLD.organization_id,OLD.uploaded_by,OLD.version_label,OLD.primary_type,OLD.created_at)
 THEN RAISE EXCEPTION 'Revision identity immutable'; END IF;
 IF NEW.status=OLD.status THEN RETURN NEW; END IF;
 IF NOT ((OLD.status='Draft' AND NEW.status='Uploaded')
 OR (OLD.status IN ('Uploaded','NeedsFix','Failed') AND NEW.status='Processing')
 OR (OLD.status='Processing' AND NEW.status IN ('ReadyForScenario','NeedsFix','Failed'))
 OR (OLD.status='ReadyForScenario' AND NEW.status='ConfirmedForTraining')
 OR (OLD.status IN ('NeedsFix','Failed','ReadyForScenario','ConfirmedForTraining','Rejected') AND NEW.status='Superseded'))
 THEN RAISE EXCEPTION 'Invalid revision transition % -> %',OLD.status,NEW.status; END IF;
 IF NEW.status='Uploaded' AND NOT EXISTS(SELECT 1 FROM source_documents WHERE revision_id=NEW.id AND quarantine_status='Accepted')
 THEN RAISE EXCEPTION 'Uploaded requires accepted IFC source'; END IF;
 IF NEW.status='ReadyForScenario' AND NOT EXISTS(SELECT 1 FROM validation_runs v JOIN processing_jobs j ON j.id=v.job_id
 WHERE v.revision_id=NEW.id AND v.kind='Geometry' AND v.outcome='Passed' AND j.status='Succeeded')
 THEN RAISE EXCEPTION 'ReadyForScenario requires successful geometry QA/job'; END IF;
 IF NEW.status='ConfirmedForTraining' AND NOT EXISTS(SELECT 1 FROM revision_reviews WHERE revision_id=NEW.id AND action='ConfirmForTraining')
 THEN RAISE EXCEPTION 'Confirmation review required'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER revision_state BEFORE INSERT OR UPDATE ON revisions FOR EACH ROW EXECUTE FUNCTION enforce_revision_status_transition();

CREATE FUNCTION validate_processing_job() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Queued' THEN RAISE EXCEPTION 'Job starts Queued'; END IF;
   IF NOT EXISTS(SELECT 1 FROM source_documents WHERE id=NEW.source_document_id AND quarantine_status='Accepted')
   THEN RAISE EXCEPTION 'Job requires accepted source'; END IF;
   IF NEW.kind='Geometry' AND r.status NOT IN ('Uploaded','Processing','NeedsFix','Failed')
   THEN RAISE EXCEPTION 'Geometry job not allowed for immutable ready revision'; END IF;
   IF NEW.kind='Scenario' AND (r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') OR NOT EXISTS
   (SELECT 1 FROM scenario_versions WHERE id=NEW.scenario_version_id AND revision_id=r.id))
   THEN RAISE EXCEPTION 'Scenario job requires matching ready revision/version'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','lease_owner','heartbeat_at','started_at','finished_at','error_message'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','lease_owner','heartbeat_at','started_at','finished_at','error_message'])
   THEN RAISE EXCEPTION 'Job identity immutable'; END IF;
   IF OLD.status IN ('Succeeded','Failed') THEN RAISE EXCEPTION 'Terminal job immutable; retry with new job'; END IF;
   IF NOT (NEW.status=OLD.status OR (OLD.status='Queued' AND NEW.status='Running') OR (OLD.status='Running' AND NEW.status IN ('Succeeded','Failed')))
   THEN RAISE EXCEPTION 'Invalid job transition'; END IF;
 END IF;
 IF NEW.status='Running' AND (NEW.started_at IS NULL OR NULLIF(btrim(NEW.lease_owner),'') IS NULL OR NEW.heartbeat_at IS NULL)
 THEN RAISE EXCEPTION 'Running job requires start and lease heartbeat'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER processing_job_write BEFORE INSERT OR UPDATE ON processing_jobs FOR EACH ROW EXECUTE FUNCTION validate_processing_job();

CREATE FUNCTION validate_revision_child() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions; j processing_jobs;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_TABLE_NAME='revision_floors' THEN
   IF r.status<>'Processing' OR EXISTS(SELECT 1 FROM validation_runs WHERE revision_id=r.id AND kind='Geometry' AND outcome='Passed')
   THEN RAISE EXCEPTION 'Floor snapshot requires Processing revision before geometry QA passes'; END IF;
 END IF;
 IF TG_TABLE_NAME='annotation_sets' THEN
   IF r.status NOT IN ('Processing','ReadyForScenario','ConfirmedForTraining') THEN RAISE EXCEPTION 'Invalid annotation revision'; END IF;
   PERFORM assert_org_user(NEW.created_by,r.organization_id);
 END IF;
 IF TG_TABLE_NAME='revision_artifacts' THEN
   SELECT * INTO STRICT j FROM processing_jobs WHERE id=NEW.job_id;
   IF j.status<>'Running' OR r.status NOT IN ('Processing','ReadyForScenario','ConfirmedForTraining')
   THEN RAISE EXCEPTION 'Artifact requires a running job and usable revision'; END IF;
   IF EXISTS(SELECT 1 FROM validation_runs WHERE job_id=j.id) THEN RAISE EXCEPTION 'Job artifacts frozen after validation'; END IF;
   IF (NEW.artifact_type='CandidatePackage')<>(j.kind='Scenario') THEN RAISE EXCEPTION 'Artifact/job kind mismatch'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER floor_insert BEFORE INSERT ON revision_floors FOR EACH ROW EXECUTE FUNCTION validate_revision_child();
CREATE TRIGGER annotation_insert BEFORE INSERT ON annotation_sets FOR EACH ROW EXECUTE FUNCTION validate_revision_child();
CREATE TRIGGER artifact_insert BEFORE INSERT ON revision_artifacts FOR EACH ROW EXECUTE FUNCTION validate_revision_child();

CREATE FUNCTION validate_scenario_version_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') THEN RAISE EXCEPTION 'Scenario requires ready geometry'; END IF;
 PERFORM assert_org_user(NEW.created_by,NEW.organization_id);
 IF NEW.schema_version<>'1.0' OR NEW.npc_config<>'{}'::jsonb
 OR jsonb_typeof(NEW.spawn_config->'nodeId') IS DISTINCT FROM 'string'
 OR NULLIF(btrim(NEW.spawn_config->>'nodeId'),'') IS NULL
 OR jsonb_typeof(NEW.goal_config->'exitIds') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'Invalid Phase 1 scenario schema/spawn/goals/NPC'; END IF;
 IF jsonb_array_length(NEW.goal_config->'exitIds')=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(NEW.goal_config->'exitIds') x
 WHERE jsonb_typeof(x)<>'string' OR btrim(x#>>'{}')='') THEN RAISE EXCEPTION 'At least one nonempty exit ID required'; END IF;
 IF NOT (NEW.routing_config ?& ARRAY['hazardWeight','portalWeight'])
 OR jsonb_typeof(NEW.routing_config->'hazardWeight') IS DISTINCT FROM 'number'
 OR jsonb_typeof(NEW.routing_config->'portalWeight') IS DISTINCT FROM 'number'
 THEN RAISE EXCEPTION 'Numeric routing weights required'; END IF;
 IF (NEW.routing_config->>'hazardWeight')::numeric<0 OR (NEW.routing_config->>'portalWeight')::numeric<0
 THEN RAISE EXCEPTION 'Routing weights must be nonnegative'; END IF;
 -- Server fingerprint of PostgreSQL canonical JSONB; do not reimplement this serialization in Flutter/Unity.
 NEW.scenario_hash:=encode(digest(convert_to((to_jsonb(NEW)-ARRAY['id','scenario_hash','created_at','created_by'])::text,'UTF8'),'sha256'),'hex');
 RETURN NEW;
END $$;
CREATE TRIGGER scenario_insert BEFORE INSERT ON scenario_versions FOR EACH ROW EXECUTE FUNCTION validate_scenario_version_write();

CREATE FUNCTION validate_qa_run() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE j processing_jobs; s scenario_versions; r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT j FROM processing_jobs WHERE id=NEW.job_id;
 IF j.status<>'Running' OR j.kind<>NEW.kind THEN RAISE EXCEPTION 'QA requires matching running job'; END IF;
 IF NEW.kind='Geometry' AND NEW.outcome='Passed' THEN
   IF NOT EXISTS(SELECT 1 FROM revision_floors WHERE revision_id=NEW.revision_id)
   OR (SELECT count(DISTINCT artifact_type) FROM revision_artifacts WHERE job_id=j.id AND artifact_type IN ('Geometry','Graph','HazardGrid'))<>3
   THEN RAISE EXCEPTION 'Geometry QA needs floors, geometry, graph and hazard artifacts'; END IF;
 END IF;
 IF NEW.kind='Scenario' THEN
   SELECT * INTO STRICT s FROM scenario_versions WHERE id=NEW.scenario_version_id;
   IF s.revision_id<>NEW.revision_id OR s.id<>j.scenario_version_id OR s.scenario_hash<>NEW.scenario_hash
   OR NOT EXISTS(SELECT 1 FROM revision_artifacts WHERE id=NEW.candidate_artifact_id AND job_id=j.id AND artifact_type='CandidatePackage')
   THEN RAISE EXCEPTION 'QA scenario/hash/candidate mismatch'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER qa_insert BEFORE INSERT ON validation_runs FOR EACH ROW EXECUTE FUNCTION validate_qa_run();

CREATE FUNCTION validate_revision_review_action() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions; v validation_runs;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id FOR UPDATE;
 PERFORM assert_org_user(NEW.reviewed_by,r.organization_id);
 SELECT * INTO STRICT v FROM validation_runs WHERE id=NEW.validation_run_id;
 IF r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') OR v.kind<>'Scenario'
 OR v.revision_id<>r.id OR v.scenario_version_id<>NEW.scenario_version_id
 OR v.annotation_set_id IS DISTINCT FROM NEW.annotation_set_id
 OR NOT EXISTS(SELECT 1 FROM processing_jobs WHERE id=v.job_id AND status='Succeeded')
 THEN RAISE EXCEPTION 'Review requires exact scenario QA and successful job'; END IF;
 IF NEW.action='ConfirmForTraining' AND v.outcome<>'Passed' THEN RAISE EXCEPTION 'Cannot confirm failed QA'; END IF;
 IF NEW.action='Rejected' AND EXISTS(SELECT 1 FROM revision_reviews WHERE scenario_version_id=NEW.scenario_version_id AND action='ConfirmForTraining')
 THEN RAISE EXCEPTION 'Confirmed scenario immutable; revoke its release if necessary'; END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION apply_revision_review_action() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.action='ConfirmForTraining' THEN UPDATE revisions SET status='ConfirmedForTraining'
 WHERE id=NEW.revision_id AND status='ReadyForScenario'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER review_validate BEFORE INSERT ON revision_reviews FOR EACH ROW EXECUTE FUNCTION validate_revision_review_action();
CREATE TRIGGER review_apply AFTER INSERT ON revision_reviews FOR EACH ROW EXECUTE FUNCTION apply_revision_review_action();
CREATE FUNCTION confirm_revision_for_training(p_revision UUID,p_scenario UUID,p_actor UUID,p_validation UUID,p_message TEXT DEFAULT NULL)
RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE v_id UUID;
BEGIN
 INSERT INTO revision_reviews(revision_id,scenario_version_id,reviewed_by,validation_run_id,annotation_set_id,action,review_message)
 SELECT p_revision,p_scenario,p_actor,v.id,v.annotation_set_id,'ConfirmForTraining',p_message FROM validation_runs v WHERE v.id=p_validation
 RETURNING id INTO v_id;
 IF v_id IS NULL THEN RAISE EXCEPTION 'Validation run missing'; END IF;
 RETURN v_id;
END $$;

CREATE FUNCTION validate_release_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 PERFORM lock_content(NEW.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Built' OR r.status<>'ConfirmedForTraining' OR NOT EXISTS(SELECT 1 FROM revision_reviews
   WHERE id=NEW.confirmation_review_id AND revision_id=NEW.revision_id AND scenario_version_id=NEW.scenario_version_id AND action='ConfirmForTraining')
   THEN RAISE EXCEPTION 'Built release requires exact confirmed revision/scenario/review'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','published_by','published_at','revoked_by','revoked_at','revoked_reason','updated_at'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','published_by','published_at','revoked_by','revoked_at','revoked_reason','updated_at'])
   THEN RAISE EXCEPTION 'Release snapshot immutable'; END IF;
   IF OLD.published_at IS NOT NULL AND (NEW.published_at,NEW.published_by) IS DISTINCT FROM (OLD.published_at,OLD.published_by)
   THEN RAISE EXCEPTION 'Publish provenance immutable'; END IF;
   IF OLD.status='Revoked' AND (to_jsonb(NEW)-'updated_at') IS DISTINCT FROM (to_jsonb(OLD)-'updated_at')
   THEN RAISE EXCEPTION 'Revoked release terminal'; END IF;
   IF NEW.status<>OLD.status AND NOT ((OLD.status='Built' AND NEW.status IN ('Published','Revoked'))
   OR (OLD.status='Published' AND NEW.status IN ('Superseded','Revoked')) OR (OLD.status='Superseded' AND NEW.status='Revoked'))
   THEN RAISE EXCEPTION 'Invalid release transition'; END IF;
   IF NEW.status<>'Published' AND EXISTS(SELECT 1 FROM release_qr_codes WHERE release_id=NEW.id AND is_active)
   THEN RAISE EXCEPTION 'Deactivate every QR before closing release'; END IF;
   IF NEW.status='Published' AND OLD.status<>'Published' THEN
     PERFORM assert_org_user(NEW.published_by,NEW.organization_id);
     IF r.status<>'ConfirmedForTraining' OR NOT EXISTS(SELECT 1 FROM release_packages WHERE release_id=NEW.id)
     OR NOT EXISTS(SELECT 1 FROM trainings WHERE release_id=NEW.id AND status='Active')
     OR NOT EXISTS(SELECT 1 FROM buildings WHERE id=NEW.building_id AND is_active AND deleted_at IS NULL)
     THEN RAISE EXCEPTION 'Publish requires usable revision/building, package and Active Training'; END IF;
   END IF;
 END IF;
 IF NEW.status='Revoked' AND (TG_OP='INSERT' OR OLD.status<>'Revoked') THEN PERFORM assert_org_user(NEW.revoked_by,NEW.organization_id); END IF;
 IF NEW.safety_thresholds IS DISTINCT FROM (SELECT safety_thresholds FROM scenario_versions WHERE id=NEW.scenario_version_id)
 THEN RAISE EXCEPTION 'Release thresholds must match confirmed scenario'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER release_write BEFORE INSERT OR UPDATE ON releases FOR EACH ROW EXECUTE FUNCTION validate_release_write();
CREATE FUNCTION validate_package() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; a revision_artifacts; v validation_runs;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 SELECT v1.* INTO STRICT v FROM validation_runs v1 JOIN revision_reviews rv ON rv.validation_run_id=v1.id WHERE rv.id=r.confirmation_review_id;
 SELECT * INTO STRICT a FROM revision_artifacts WHERE id=NEW.candidate_artifact_id;
 IF r.status<>'Built' OR a.id<>v.candidate_artifact_id OR a.sha256_hash<>NEW.checksum_sha256 OR a.size_bytes<>NEW.package_size_bytes
 OR a.object_key<>NEW.package_url THEN RAISE EXCEPTION 'Package must match exact confirmed candidate artifact'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER package_insert BEFORE INSERT ON release_packages FOR EACH ROW EXECUTE FUNCTION validate_package();

CREATE FUNCTION validate_training_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 IF TG_OP='INSERT' THEN
   PERFORM assert_org_user(NEW.created_by,NEW.organization_id);
   IF NEW.status NOT IN ('Draft','Active') THEN RAISE EXCEPTION 'Training starts Draft or Active'; END IF;
 ELSE
   IF (NEW.id,NEW.release_id,NEW.scenario_version_id,NEW.organization_id,NEW.created_by,NEW.created_at)
   IS DISTINCT FROM (OLD.id,OLD.release_id,OLD.scenario_version_id,OLD.organization_id,OLD.created_by,OLD.created_at)
   THEN RAISE EXCEPTION 'Training binding immutable'; END IF;
   IF NEW.status<>OLD.status AND NOT ((OLD.status='Draft' AND NEW.status IN ('Active','Archived'))
   OR (OLD.status='Active' AND NEW.status='Closed') OR (OLD.status='Closed' AND NEW.status='Archived'))
   THEN RAISE EXCEPTION 'Invalid Training transition'; END IF;
   IF (NEW.mode,NEW.allowed_modes,NEW.max_attempts,NEW.start_date,NEW.end_date)
   IS DISTINCT FROM (OLD.mode,OLD.allowed_modes,OLD.max_attempts,OLD.start_date,OLD.end_date)
   AND (EXISTS(SELECT 1 FROM release_qr_codes WHERE training_id=OLD.id) OR EXISTS(SELECT 1 FROM sessions WHERE training_id=OLD.id))
   THEN RAISE EXCEPTION 'Training policy frozen once any QR/session exists'; END IF;
   IF NEW.status<>'Active' AND EXISTS(SELECT 1 FROM release_qr_codes WHERE training_id=NEW.id AND is_active)
   THEN RAISE EXCEPTION 'Deactivate QR before closing Training'; END IF;
 END IF;
 IF NEW.status IN ('Draft','Active') AND r.status NOT IN ('Built','Published') THEN RAISE EXCEPTION 'Usable release required to activate Training'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER training_write BEFORE INSERT OR UPDATE ON trainings FOR EACH ROW EXECUTE FUNCTION validate_training_write();
CREATE FUNCTION validate_release_qr_code() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; t trainings;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 SELECT * INTO STRICT t FROM trainings WHERE id=NEW.training_id;
 IF TG_OP='UPDATE' THEN
   IF (to_jsonb(NEW)-'is_active') IS DISTINCT FROM (to_jsonb(OLD)-'is_active') OR (NOT OLD.is_active AND NEW.is_active)
   THEN RAISE EXCEPTION 'QR immutable; rotate with a new token/row'; END IF;
 ELSE PERFORM assert_org_user(NEW.created_by,NEW.organization_id); END IF;
 IF NEW.revision_floor_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM revision_floors WHERE id=NEW.revision_floor_id AND revision_id=r.revision_id)
 THEN RAISE EXCEPTION 'QR floor must belong to pinned revision'; END IF;
 IF NEW.is_active AND (r.status<>'Published' OR t.status<>'Active' OR (NEW.expires_at IS NOT NULL AND NEW.expires_at<=clock_timestamp()))
 THEN RAISE EXCEPTION 'Active QR requires Published release, Active Training and future expiry'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER qr_write BEFORE INSERT OR UPDATE ON release_qr_codes FOR EACH ROW EXECUTE FUNCTION validate_release_qr_code();

CREATE FUNCTION validate_training_session() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; t trainings; q release_qr_codes; res session_results;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 IF TG_OP='INSERT' THEN
   PERFORM lock_content(r.building_id);
   SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
   SELECT * INTO STRICT t FROM trainings WHERE id=NEW.training_id;
   SELECT * INTO STRICT q FROM release_qr_codes WHERE id=NEW.qr_code_id;
   IF NEW.status<>'Created' OR NEW.launched_at IS NOT NULL OR NEW.terminal_reason IS NOT NULL
   THEN RAISE EXCEPTION 'Session starts Created'; END IF;
   NEW.started_at:=clock_timestamp();
   IF NOT EXISTS(SELECT 1 FROM users WHERE id=NEW.trainee_user_id AND role='Trainee' AND is_active AND deleted_at IS NULL)
   OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=NEW.organization_id AND is_active AND deleted_at IS NULL)
   OR NOT EXISTS(SELECT 1 FROM buildings WHERE id=r.building_id AND is_active AND deleted_at IS NULL)
   THEN RAISE EXCEPTION 'Active Trainee and content organization/building required'; END IF;
   IF r.status<>'Published' OR t.status<>'Active' OR NOT q.is_active OR (q.expires_at IS NOT NULL AND q.expires_at<=clock_timestamp())
   OR (t.start_date IS NOT NULL AND t.start_date>clock_timestamp()) OR (t.end_date IS NOT NULL AND t.end_date<=clock_timestamp())
   OR NOT NEW.mode::text=ANY(t.allowed_modes) THEN RAISE EXCEPTION 'QR/Training/release/mode/window unavailable'; END IF;
   IF NEW.mode='Assessment' AND t.max_attempts IS NOT NULL AND (SELECT count(*) FROM sessions WHERE training_id=t.id
   AND trainee_user_id=NEW.trainee_user_id AND mode='Assessment')>=t.max_attempts THEN RAISE EXCEPTION 'Assessment attempts exhausted'; END IF;
   IF NEW.scenario_hash IS DISTINCT FROM (SELECT scenario_hash FROM scenario_versions WHERE id=NEW.scenario_version_id)
   OR NEW.release_hash IS DISTINCT FROM (SELECT checksum_sha256 FROM release_packages WHERE release_id=r.id)
   THEN RAISE EXCEPTION 'Session content hash mismatch'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','launched_at','ended_at','terminal_reason','content_status_at_completion'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','launched_at','ended_at','terminal_reason','content_status_at_completion'])
   THEN RAISE EXCEPTION 'Session bindings, start and hashes immutable'; END IF;
   IF OLD.status NOT IN ('Created','Launching','Running') THEN RAISE EXCEPTION 'Terminal session immutable'; END IF;
   IF NEW.status=OLD.status THEN
     IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Lifecycle metadata changes require transition'; END IF;
     RETURN NEW;
   END IF;
   IF NOT ((OLD.status='Created' AND NEW.status IN ('Launching','Aborted','Abandoned'))
   OR (OLD.status='Launching' AND NEW.status IN ('Running','Crashed','Aborted','Abandoned'))
   OR (OLD.status='Running' AND NEW.status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable','Crashed','Aborted','Abandoned')))
   THEN RAISE EXCEPTION 'Invalid Session transition'; END IF;
   IF NEW.status='Running' THEN NEW.launched_at:=clock_timestamp();
   ELSIF NEW.launched_at IS DISTINCT FROM OLD.launched_at THEN RAISE EXCEPTION 'Launch time immutable'; END IF;
   IF NEW.status NOT IN ('Created','Launching','Running') THEN
     NEW.ended_at:=clock_timestamp(); NEW.content_status_at_completion:=r.status;
     IF NEW.status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable') THEN
       SELECT * INTO res FROM session_results WHERE session_id=NEW.id;
       IF NOT FOUND THEN RAISE EXCEPTION 'Result required before completing Session'; END IF;
       IF (NEW.status='ScenarioUnsurvivable')=res.reached_exit THEN RAISE EXCEPTION 'Outcome/result mismatch'; END IF;
       IF NEW.status='CompletedWithSupersededRelease' AND r.status<>'Superseded' THEN RAISE EXCEPTION 'Release is not superseded'; END IF;
       IF NEW.status='Completed' AND r.status='Superseded' THEN RAISE EXCEPTION 'Use CompletedWithSupersededRelease'; END IF;
     END IF;
   END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER session_write BEFORE INSERT OR UPDATE ON sessions FOR EACH ROW EXECUTE FUNCTION validate_training_session();

CREATE FUNCTION validate_session_child() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE s sessions; n BIGINT; last_seq BIGINT;
BEGIN
 SELECT * INTO STRICT s FROM sessions WHERE id=NEW.session_id FOR UPDATE;
 IF s.status<>'Running' THEN RAISE EXCEPTION 'Events/results/checkpoints require Running Session'; END IF;
 IF TG_TABLE_NAME='session_events' THEN
   IF NEW.schema_version<>'1.0' THEN RAISE EXCEPTION 'Unsupported event schema'; END IF;
 END IF;
 IF TG_TABLE_NAME='session_checkpoints' THEN
   IF NEW.release_hash<>s.release_hash OR NEW.scenario_hash<>s.scenario_hash THEN RAISE EXCEPTION 'Checkpoint content hash mismatch'; END IF;
 END IF;
 IF TG_TABLE_NAME='session_results' THEN
   SELECT count(*),COALESCE(max(sequence_number),0) INTO n,last_seq FROM session_events WHERE session_id=s.id;
   IF n<>NEW.last_event_sequence OR last_seq<>NEW.last_event_sequence THEN RAISE EXCEPTION 'Completion requires contiguous event sequence 1..last'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER event_insert BEFORE INSERT ON session_events FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE TRIGGER result_insert BEFORE INSERT ON session_results FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE TRIGGER checkpoint_insert BEFORE INSERT ON session_checkpoints FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE FUNCTION verify_result_committed_terminal() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM sessions WHERE id=NEW.session_id AND status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable'))
 THEN RAISE EXCEPTION 'Result and terminal Session must commit together'; END IF;
 RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER result_terminal AFTER INSERT ON session_results DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION verify_result_committed_terminal();

CREATE FUNCTION start_training_session(p_actor UUID,p_qr UUID,p_device UUID,p_mode session_mode_enum,p_start_key UUID,
 p_app TEXT,p_unity TEXT,p_protocol TEXT DEFAULT '1.0') RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE old_session sessions; q release_qr_codes; r releases; s_id UUID;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended(p_actor::text||':'||p_start_key::text,602));
 SELECT * INTO old_session FROM sessions WHERE trainee_user_id=p_actor AND start_key=p_start_key;
 IF FOUND THEN
   IF (old_session.qr_code_id,old_session.device_id,old_session.mode,old_session.app_version,old_session.unity_version,old_session.protocol_version)
   IS DISTINCT FROM (p_qr,p_device,p_mode,p_app,p_unity,p_protocol) THEN RAISE EXCEPTION 'Start key payload conflict'; END IF;
   RETURN old_session.id;
 END IF;
 IF p_protocol<>'1.0' THEN RAISE EXCEPTION 'Unsupported launch protocol'; END IF;
 SELECT * INTO STRICT q FROM release_qr_codes WHERE id=p_qr;
 SELECT * INTO STRICT r FROM releases WHERE id=q.release_id;
 PERFORM lock_content(r.building_id);
 INSERT INTO sessions(training_id,release_id,scenario_version_id,organization_id,trainee_user_id,device_id,qr_code_id,start_key,
 app_version,unity_version,protocol_version,scenario_hash,release_hash,mode)
 SELECT q.training_id,r.id,r.scenario_version_id,r.organization_id,p_actor,p_device,p_qr,p_start_key,p_app,p_unity,p_protocol,
 v.scenario_hash,p.checksum_sha256,p_mode FROM scenario_versions v JOIN release_packages p ON p.release_id=r.id WHERE v.id=r.scenario_version_id
 RETURNING id INTO s_id;
 IF s_id IS NULL THEN RAISE EXCEPTION 'Package unavailable'; END IF;
 RETURN s_id;
END $$;
CREATE FUNCTION append_session_event(p_actor UUID,p_session UUID,p_sequence BIGINT,p_event_id UUID,p_type TEXT,p_data JSONB,
 p_elapsed BIGINT,p_recorded TIMESTAMPTZ,p_schema TEXT DEFAULT '1.0') RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE s sessions; e session_events; new_id UUID;
BEGIN
 SELECT * INTO STRICT s FROM sessions WHERE id=p_session FOR UPDATE;
 IF s.trainee_user_id IS DISTINCT FROM p_actor THEN RAISE EXCEPTION 'Session owner mismatch'; END IF;
 SELECT * INTO e FROM session_events WHERE session_id=p_session AND (sequence_number=p_sequence OR client_event_id=p_event_id);
 IF FOUND THEN
   IF (e.sequence_number,e.client_event_id,e.event_type,e.event_data,e.elapsed_ms,e.recorded_at,e.schema_version)
   IS DISTINCT FROM (p_sequence,p_event_id,p_type,p_data,p_elapsed,p_recorded,p_schema) THEN RAISE EXCEPTION 'Event identity/payload conflict'; END IF;
   RETURN e.id;
 END IF;
 INSERT INTO session_events(session_id,sequence_number,client_event_id,event_type,event_data,elapsed_ms,recorded_at,schema_version)
 VALUES(p_session,p_sequence,p_event_id,p_type,p_data,p_elapsed,p_recorded,p_schema) RETURNING id INTO new_id;
 RETURN new_id;
END $$;
CREATE FUNCTION complete_training_session(p_actor UUID,p_session UUID,p_key UUID,p_last BIGINT,p_result JSONB,p_outcome session_status_enum)
RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE s sessions; old_result session_results; new_id UUID; release_status release_status_enum; final_status session_status_enum;
BEGIN
 IF p_outcome IS NULL OR p_outcome NOT IN ('Completed','ScenarioUnsurvivable') THEN RAISE EXCEPTION 'Unsupported completion outcome'; END IF;
 SELECT * INTO STRICT s FROM sessions WHERE id=p_session FOR UPDATE;
 IF s.trainee_user_id IS DISTINCT FROM p_actor THEN RAISE EXCEPTION 'Session owner mismatch'; END IF;
 SELECT * INTO old_result FROM session_results WHERE session_id=s.id;
 IF FOUND THEN
   IF old_result.completion_key IS DISTINCT FROM p_key OR old_result.last_event_sequence IS DISTINCT FROM p_last
   OR old_result.submission_payload IS DISTINCT FROM p_result
   OR (p_outcome='ScenarioUnsurvivable')<>(s.status='ScenarioUnsurvivable') THEN RAISE EXCEPTION 'Completion key/payload conflict'; END IF;
   RETURN old_result.id;
 END IF;
 IF p_result IS NULL OR jsonb_typeof(p_result)<>'object' OR NOT(p_result ?& ARRAY['score','timeTakenSeconds','reachedExit','rubricVersion'])
 THEN RAISE EXCEPTION 'Completion requires score, duration, reachedExit and rubricVersion'; END IF;
 INSERT INTO session_results(session_id,completion_key,last_event_sequence,submission_payload,rubric_version,score,time_taken_seconds,
 wrong_exits,hazard_exposure_score,total_distance_meters,reached_exit,exit_point_id,path_traveled)
 VALUES(s.id,p_key,p_last,p_result,p_result->>'rubricVersion',(p_result->>'score')::numeric,(p_result->>'timeTakenSeconds')::int,
 COALESCE((p_result->>'wrongExits')::int,0),COALESCE((p_result->>'hazardExposureScore')::numeric,0),
 COALESCE((p_result->>'totalDistanceMeters')::numeric,0),(p_result->>'reachedExit')::boolean,p_result->>'exitPointId',
 COALESCE(p_result->'pathTraveled','[]')) RETURNING id INTO new_id;
 SELECT status INTO STRICT release_status FROM releases WHERE id=s.release_id;
 final_status:=CASE WHEN p_outcome='Completed' AND release_status='Superseded' THEN 'CompletedWithSupersededRelease'::session_status_enum ELSE p_outcome END;
 UPDATE sessions SET status=final_status WHERE id=s.id;
 RETURN new_id;
END $$;

CREATE FUNCTION guard_identity_change() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='user_devices' THEN
   IF (NEW.id,NEW.user_id,NEW.device_uuid,NEW.created_at) IS DISTINCT FROM (OLD.id,OLD.user_id,OLD.device_uuid,OLD.created_at)
   THEN RAISE EXCEPTION 'Device registration identity immutable'; END IF;
 ELSIF TG_TABLE_NAME='users' THEN
   IF (NEW.id,NEW.role,NEW.organization_id) IS DISTINCT FROM (OLD.id,OLD.role,OLD.organization_id)
   THEN RAISE EXCEPTION 'Account role/org immutable; provision a separate account'; END IF;
 ELSIF TG_TABLE_NAME='building_floors' THEN
   IF (NEW.id,NEW.building_id,NEW.organization_id,NEW.floor_number) IS DISTINCT FROM (OLD.id,OLD.building_id,OLD.organization_id,OLD.floor_number)
   THEN RAISE EXCEPTION 'Floor identity immutable; snapshot changes in new revision'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER user_identity BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE TRIGGER device_identity BEFORE UPDATE ON user_devices FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE TRIGGER floor_identity BEFORE UPDATE ON building_floors FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE FUNCTION record_core_audit() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE v JSONB:=to_jsonb(NEW); previous JSONB; v_org UUID; v_actor UUID; v_action audit_action_enum;
BEGIN
 IF TG_OP='UPDATE' THEN previous:=to_jsonb(OLD); END IF;
 v_org:=(v->>'organization_id')::uuid;
 IF v_org IS NULL AND v ? 'revision_id' THEN SELECT organization_id INTO v_org FROM revisions WHERE id=(v->>'revision_id')::uuid; END IF;
 IF v_org IS NULL AND v ? 'session_id' THEN SELECT organization_id INTO v_org FROM sessions WHERE id=(v->>'session_id')::uuid; END IF;
 v_actor:=COALESCE((v->>'reviewed_by')::uuid,(v->>'uploaded_by')::uuid,(v->>'created_by')::uuid,(v->>'trainee_user_id')::uuid);
 v_action:=CASE WHEN TG_OP='INSERT' THEN 'Create'::audit_action_enum ELSE 'Update'::audit_action_enum END;
 IF TG_TABLE_NAME='source_documents' AND TG_OP='INSERT' THEN v_action:='Upload'; END IF;
 IF TG_TABLE_NAME='revision_reviews' THEN v_action:=CASE WHEN NEW.action='ConfirmForTraining' THEN 'ConfirmForTraining'::audit_action_enum ELSE 'Reject'::audit_action_enum END; END IF;
 IF TG_TABLE_NAME='releases' AND TG_OP='UPDATE' THEN
   IF NEW.status='Published' AND OLD.status<>'Published' THEN v_action:='Publish'; v_actor:=NEW.published_by; END IF;
   IF NEW.status='Revoked' AND OLD.status<>'Revoked' THEN v_action:='Revoke'; v_actor:=NEW.revoked_by; END IF;
 END IF;
 -- For UPDATE creator is not necessarily the actor. API must add its authenticated command audit/correlation.
 IF TG_OP='UPDATE' AND v_action='Update' THEN v_actor:=NULL; END IF;
 INSERT INTO audit_logs(user_id,organization_id,actor_type,action,target_entity,target_id,old_values,new_values)
 VALUES(v_actor,v_org,CASE WHEN v_actor IS NULL THEN 'System' ELSE 'User' END,v_action,TG_TABLE_NAME,(v->>'id')::uuid,
 CASE WHEN previous IS NULL THEN NULL ELSE jsonb_build_object('status',previous->'status','isActive',previous->'is_active') END,
 jsonb_build_object('status',v->'status','isActive',v->'is_active'));
 RETURN NEW;
END $$;
DO $$ DECLARE t TEXT; BEGIN
 FOREACH t IN ARRAY ARRAY['annotation_sets','revision_floors','scenarios','scenario_versions','revision_artifacts','validation_runs',
 'revision_issues','revision_reviews','release_packages','revision_processing_logs','session_events','session_results','session_checkpoints','debrief_artifacts','audit_logs']
 LOOP EXECUTE format('CREATE TRIGGER immutable_snapshot BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['revisions','processing_jobs','releases','trainings','release_qr_codes','sessions']
 LOOP EXECUTE format('CREATE TRIGGER retained_history BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['organizations','users','buildings','building_locations','building_floors','building_contacts','revisions','releases','trainings']
 LOOP EXECUTE format('CREATE TRIGGER set_updated_at BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION update_updated_at_column()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['buildings','revisions','source_documents','scenario_versions','revision_reviews','releases','release_qr_codes','sessions','session_results']
 LOOP EXECUTE format('CREATE TRIGGER core_audit AFTER INSERT OR UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION record_core_audit()',t); END LOOP;
END $$;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
-- Deployment must grant narrowly to backend/worker logins. No RLS claim: tenant
-- SELECT authorization remains in API; see database security/command contract.
