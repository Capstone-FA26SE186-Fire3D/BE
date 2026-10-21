-- BE#144: fet3d.editor/1 gains optional scenario-wide "wind" {directionDegrees, strength} and "routingConfig" (A* weights
-- hazardExposureWeight, congestionWeight, portalPenaltyWeight, blockedWeight). Both stay in state_snapshot, which scenario_hash covers,
-- so content review and package input carry them unchanged. The derived routing_config projection now also exposes the weights
-- (key "weights", absent when the draft has none); goalObjectIds is unchanged. Existing snapshots are not rewritten.
CREATE OR REPLACE FUNCTION project_editor_v1_snapshot() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
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
 NEW.routing_config:=jsonb_build_object('goalObjectIds',NEW.routing_config)
  ||CASE WHEN jsonb_typeof(s->'routingConfig')='object' THEN jsonb_build_object('weights',s->'routingConfig') ELSE '{}'::jsonb END;
 NEW.time_limit_seconds:=(s->>'timeLimitSeconds')::integer;
 NEW.scoring_config:=jsonb_build_object('timeLimitSeconds',NEW.time_limit_seconds);
 NEW.mode_policy:=COALESCE(NULLIF(s->'modePolicy','null'::jsonb),'{}');
 NEW.safety_thresholds:='{}';
 NEW.random_seed:=COALESCE((s->>'randomSeed')::bigint,0);
 NEW.replan_interval_seconds:=COALESCE((s->>'replanIntervalSeconds')::integer,0);
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION project_editor_v1_snapshot() FROM PUBLIC;
