-- Additive editor contract fet3d.editor/1. Legacy drafts, snapshots and catalog rows keep their stored shape.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM scenario_versions WHERE state_snapshot->>'schemaVersion' IS NOT NULL) THEN
  RAISE EXCEPTION 'Editor contract preflight: versioned snapshots already exist; operator review required';
 END IF;
END $$;
ALTER TABLE runtime_compatibility_catalog ADD COLUMN capability_contracts jsonb NOT NULL DEFAULT '{}'::jsonb
 CONSTRAINT runtime_capability_contracts_object CHECK (jsonb_typeof(capability_contracts)='object');
COMMENT ON COLUMN runtime_compatibility_catalog.capability_contracts IS
 'Map capabilityId -> {version, objectKinds, parameters}. Keys must be listed in capabilities; operators publish contracts, no seed.';

-- Derived projection for versioned snapshots; state_snapshot and scenario_hash remain the authoritative content.
CREATE FUNCTION project_editor_v1_snapshot() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE s jsonb:=NEW.state_snapshot;
BEGIN
 IF s IS NULL OR s->>'schemaVersion' IS DISTINCT FROM 'fet3d.editor/1' THEN RETURN NEW; END IF;
 IF jsonb_typeof(s->'objects') IS DISTINCT FROM 'array' OR jsonb_typeof(s->'timeLimitSeconds') IS DISTINCT FROM 'number' THEN
  RAISE EXCEPTION 'Versioned snapshot requires objects and timeLimitSeconds';
 END IF;
 SELECT COALESCE(jsonb_agg(o ORDER BY n) FILTER (WHERE o->>'kind'='Spawn'),'[]'),
        COALESCE(jsonb_agg(o ORDER BY n) FILTER (WHERE o->>'kind'='Goal'),'[]'),
        COALESCE(jsonb_agg(o ORDER BY n) FILTER (WHERE o->>'kind'='Hazard'),'[]'),
        COALESCE(jsonb_agg(o ORDER BY n) FILTER (WHERE o->>'kind'='Npc'),'[]'),
        COALESCE(jsonb_agg(o ORDER BY n) FILTER (WHERE o->>'kind'='BlockedElement'),'[]'),
        COALESCE(jsonb_agg(o->'id' ORDER BY n) FILTER (WHERE o->>'kind'='Goal'),'[]')
   INTO NEW.spawn_config,NEW.goal_config,NEW.fire_source_config,NEW.npc_config,NEW.blocked_elements,NEW.routing_config
   FROM jsonb_array_elements(s->'objects') WITH ORDINALITY x(o,n);
 NEW.routing_config:=jsonb_build_object('goalObjectIds',NEW.routing_config);
 NEW.time_limit_seconds:=(s->>'timeLimitSeconds')::integer;
 NEW.scoring_config:=jsonb_build_object('timeLimitSeconds',NEW.time_limit_seconds);
 NEW.mode_policy:=COALESCE(NULLIF(s->'modePolicy','null'::jsonb),'{}');
 NEW.safety_thresholds:='{}';
 NEW.random_seed:=COALESCE((s->>'randomSeed')::bigint,0);
 NEW.replan_interval_seconds:=COALESCE((s->>'replanIntervalSeconds')::integer,0);
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION project_editor_v1_snapshot() FROM PUBLIC;
CREATE TRIGGER project_editor_v1_snapshot BEFORE INSERT ON scenario_versions FOR EACH ROW EXECUTE FUNCTION project_editor_v1_snapshot();

CREATE TEMP TABLE editor_contract_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO editor_contract_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;
CREATE OR REPLACE FUNCTION scenario_reference_issues(p_revision uuid,p_state jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE issues jsonb:='[]';anchor text;cap text;available jsonb;catalog runtime_compatibility_catalog;
 geo revision_artifacts;floors jsonb;anchors jsonb;o jsonb;n bigint;path text;contract jsonb;
BEGIN
 IF p_state->'schemaVersion' IS NULL THEN
  -- Legacy branch unchanged, except versioned Geometry metadata exposes anchors through semanticMapping.
  IF p_state->'objectAnchors' IS NOT NULL AND p_state->'objectAnchors'<>'null'::jsonb THEN
   IF jsonb_typeof(p_state->'objectAnchors') IS DISTINCT FROM 'array' THEN RETURN jsonb_build_array(jsonb_build_object('code','ANCHORS_INVALID','path','$.objectAnchors','message','Anchors must be an array.'));END IF;
   SELECT CASE WHEN a.metadata->>'schemaVersion'='fet3d.editor/1'
     THEN (SELECT COALESCE(jsonb_agg(m->'ifcGlobalId'),'[]') FROM jsonb_array_elements(a.metadata->'semanticMapping') m)
     ELSE a.metadata->'objectAnchors' END INTO available FROM revision_artifacts a
    JOIN processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
    JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND pa.processing_job_id=j.id AND j.current_attempt_id=pa.id
    WHERE a.revision_id=p_revision AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded'
    AND (jsonb_typeof(a.metadata->'objectAnchors')='array' OR a.metadata->>'schemaVersion'='fet3d.editor/1' AND jsonb_typeof(a.metadata->'semanticMapping')='array')
    AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=j.id AND vr.processing_attempt_id=pa.id
      AND vr.revision_id=a.revision_id AND vr.kind='Geometry' AND vr.outcome='Passed'
      AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')))
    ORDER BY a.created_at DESC,a.id LIMIT 1;
   FOR anchor IN SELECT jsonb_array_elements_text(p_state->'objectAnchors') LOOP
    IF available IS NULL OR NOT available ? anchor THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','ANCHOR_NOT_FOUND','path','$.objectAnchors','message','Anchor does not exist in accepted geometry for this revision.'));END IF;
   END LOOP;
  END IF;
  IF p_state->'requiredCapabilities' IS NOT NULL AND p_state->'requiredCapabilities'<>'null'::jsonb THEN
   IF jsonb_typeof(p_state->'requiredCapabilities') IS DISTINCT FROM 'array' THEN RETURN issues||jsonb_build_array(jsonb_build_object('code','CAPABILITIES_INVALID','path','$.requiredCapabilities','message','Capabilities must be an array.'));END IF;
   IF jsonb_array_length(p_state->'requiredCapabilities')>0 THEN
    SELECT * INTO catalog FROM runtime_compatibility_catalog WHERE runtime_version=p_state->>'runtimeVersion' AND is_active ORDER BY created_at DESC,id LIMIT 1;
    IF catalog.id IS NULL THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','RUNTIME_NOT_SUPPORTED','path','$.runtimeVersion','message','Select an active runtime in the server catalog.'));
    ELSE FOR cap IN SELECT jsonb_array_elements_text(p_state->'requiredCapabilities') LOOP
     IF NOT catalog.capabilities ? cap THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_NOT_SUPPORTED','path','$.requiredCapabilities','message','Runtime does not provide this capability.'));END IF;
    END LOOP;END IF;
   END IF;
  END IF;
  RETURN issues;
 END IF;
 IF p_state->>'schemaVersion' IS DISTINCT FROM 'fet3d.editor/1' THEN
  RETURN jsonb_build_array(jsonb_build_object('code','EDITOR_SCHEMA_VERSION_UNSUPPORTED','path','$.schemaVersion','message','Schema version is not supported.'));
 END IF;
 IF jsonb_typeof(p_state->'objects') IS DISTINCT FROM 'array' THEN
  RETURN jsonb_build_array(jsonb_build_object('code','FIELD_REQUIRED','path','$.objects','message','Objects must be an array.'));
 END IF;
 -- Versioned drafts pin one accepted Geometry artifact of this revision: current succeeded attempt, Passed QA, no blocking issue.
 IF p_state->'geometry'->>'artifactId' ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
  SELECT a.* INTO geo FROM revision_artifacts a
   JOIN processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
   JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND pa.processing_job_id=j.id AND j.current_attempt_id=pa.id
   WHERE a.id=(p_state->'geometry'->>'artifactId')::uuid AND a.revision_id=p_revision AND a.artifact_type='preview_glb'
   AND a.sha256_hash=p_state->'geometry'->>'sha256Hash' AND a.metadata->>'schemaVersion'='fet3d.editor/1'
   AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded'
   AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=j.id AND vr.processing_attempt_id=pa.id
     AND vr.revision_id=a.revision_id AND vr.kind='Geometry' AND vr.outcome='Passed'
     AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')));
 END IF;
 IF geo.id IS NULL THEN
  issues:=issues||jsonb_build_array(jsonb_build_object('code','GEOMETRY_NOT_ACCEPTED','path','$.geometry','message','Pin the current accepted versioned Geometry artifact of this revision.'));
 ELSE
  SELECT COALESCE(jsonb_object_agg(f->>'id',true),'{}') INTO floors FROM jsonb_array_elements(geo.metadata->'floors') f WHERE f->>'id' IS NOT NULL;
  SELECT COALESCE(jsonb_object_agg(m->>'ifcGlobalId',m->>'floorId'),'{}') INTO anchors FROM jsonb_array_elements(geo.metadata->'semanticMapping') m WHERE m->>'ifcGlobalId' IS NOT NULL;
 END IF;
 FOR o,n IN SELECT value,ordinality-1 FROM jsonb_array_elements(p_state->'objects') WITH ORDINALITY LOOP
  path:='$.objects['||n||']';
  IF jsonb_typeof(o) IS DISTINCT FROM 'object' THEN CONTINUE; END IF;
  IF geo.id IS NOT NULL THEN
   IF NOT floors ? COALESCE(o->'placement'->>'floorId','') THEN
    issues:=issues||jsonb_build_array(jsonb_build_object('code','FLOOR_NOT_FOUND','path',path||'.placement.floorId','message','Floor does not exist in the pinned Geometry artifact.'));
   END IF;
   IF o ? 'anchorId' THEN
    IF NOT anchors ? COALESCE(o->>'anchorId','') THEN
     issues:=issues||jsonb_build_array(jsonb_build_object('code','ANCHOR_NOT_FOUND','path',path||'.anchorId','message','Anchor does not exist in the pinned Geometry artifact.'));
    ELSIF anchors->>(o->>'anchorId') IS DISTINCT FROM o->'placement'->>'floorId' THEN
     issues:=issues||jsonb_build_array(jsonb_build_object('code','ANCHOR_FLOOR_MISMATCH','path',path||'.anchorId','message','Anchor belongs to a different floor than the placement.'));
    END IF;
   END IF;
  END IF;
  IF jsonb_typeof(o->'capability')='object' THEN
   IF catalog.id IS NULL THEN
    SELECT * INTO catalog FROM runtime_compatibility_catalog WHERE runtime_version=p_state->>'runtimeVersion' AND is_active ORDER BY created_at DESC,id LIMIT 1;
    IF catalog.id IS NULL THEN
     RETURN issues||jsonb_build_array(jsonb_build_object('code','RUNTIME_NOT_SUPPORTED','path','$.runtimeVersion','message','Select an active runtime in the server catalog.'));
    END IF;
   END IF;
   contract:=catalog.capability_contracts->(o->'capability'->>'id');
   IF NOT catalog.capabilities ? COALESCE(o->'capability'->>'id','') OR jsonb_typeof(contract) IS DISTINCT FROM 'object' THEN
    issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_NOT_SUPPORTED','path',path||'.capability.id','message','Runtime does not publish a contract for this capability.'));
   ELSIF contract->>'version' IS DISTINCT FROM o->'capability'->>'version' THEN
    issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_VERSION_MISMATCH','path',path||'.capability.version','message','Capability version differs from the runtime contract.'));
   ELSIF jsonb_typeof(contract->'objectKinds') IS DISTINCT FROM 'array' OR NOT contract->'objectKinds' ? COALESCE(o->>'kind','') THEN
    issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_KIND_UNSUPPORTED','path',path||'.capability.id','message','Capability does not support this object kind.'));
   END IF;
  END IF;
 END LOOP;
 RETURN issues;
END $$;
REVOKE ALL ON FUNCTION scenario_reference_issues(uuid,jsonb) FROM PUBLIC;
DO $$ DECLARE r text;s record;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION scenario_reference_issues(uuid,jsonb) FROM %I',r);END IF;END LOOP;
 SELECT * INTO s FROM editor_contract_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
