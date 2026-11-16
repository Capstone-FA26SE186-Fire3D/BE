-- Trainee AI guidance per rubric criterion. Before a drill (Training context) the snapshot names each criterion of the
-- pinned scenario version with its label, description and metric so the assistant can say how to complete it; thresholds,
-- operators, weights, mandatory flags and the pass threshold stay out (the grading itself is not disclosed). After a drill
-- (Result context) the saved criterion results get the same label/description the Trainee already sees on the result
-- screen (learner_result_rubric), plus the pass threshold. Bodies otherwise as in AiContractV2; signatures, owner and
-- grants are unchanged.
CREATE TEMP TABLE ai_trainee_criteria_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_trainee_criteria_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;

CREATE OR REPLACE FUNCTION ai_trainee_training_snapshot(p_actor uuid,p_training uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('trainingId',t.id,'trainingName',t.name,'trainingDescription',t.description,'buildingId',b.id,'buildingName',b.name,
  'scenarioVersionId',sv.id,'scenarioName',sv.name,'learningObjectives',sv.learning_objectives,'learnerInstructions',sv.learner_instructions,
  'allowedModes',to_jsonb(t.allowed_modes),'timeLimitSeconds',sv.time_limit_seconds,
  'criteria',COALESCE((SELECT jsonb_agg(jsonb_strip_nulls(jsonb_build_object('label',c->>'label','description',c->>'description','metric',c->>'metric')) ORDER BY o)
   FROM jsonb_array_elements(CASE WHEN jsonb_typeof(sv.rubric->'criteria')='array' THEN sv.rubric->'criteria' ELSE '[]' END) WITH ORDINALITY e(c,o)
   WHERE jsonb_typeof(c)='object'),'[]'))
 FROM trainings t JOIN releases rel ON rel.id=t.release_id JOIN buildings b ON b.id=rel.building_id JOIN scenario_versions sv ON sv.id=rel.scenario_version_id
  JOIN release_build_provenance pin ON pin.release_id=rel.id JOIN scenario_content_reviews review ON review.id=pin.content_review_id AND review.status='Approved'
 WHERE t.id=p_training AND t.status='Active' AND rel.status='Published' AND b.is_active AND b.deleted_at IS NULL
  AND (t.start_date IS NULL OR t.start_date<=clock_timestamp()) AND (t.end_date IS NULL OR t.end_date>clock_timestamp())
  AND (b.visibility='Public' OR EXISTS(SELECT 1 FROM building_participation_grants g WHERE g.building_id=b.id AND g.trainee_user_id=p_actor AND g.access_revision=b.access_revision))
 LIMIT 1
$$;

CREATE OR REPLACE FUNCTION ai_trainee_result_snapshot(p_actor uuid,p_session uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('sessionId',s.id,'trainingId',s.training_id,'mode',r.mode,'outcome',r.outcome,'reason',r.outcome_reason,'scorePercent',r.score,
  'passThreshold',CASE WHEN jsonb_typeof(v.rubric->'pass_threshold')='number' THEN v.rubric->'pass_threshold' END,
  'criteria',CASE WHEN jsonb_typeof(r.criterion_results)='array' THEN
   (SELECT COALESCE(jsonb_agg(CASE WHEN jsonb_typeof(cr)='object' THEN cr||jsonb_strip_nulls(jsonb_build_object('label',rc->>'label','description',rc->>'description')) ELSE cr END ORDER BY o),'[]')
    FROM jsonb_array_elements(r.criterion_results) WITH ORDINALITY e(cr,o)
    LEFT JOIN LATERAL (SELECT x FROM jsonb_array_elements(CASE WHEN jsonb_typeof(v.rubric->'criteria')='array' THEN v.rubric->'criteria' ELSE '[]' END) x
     WHERE jsonb_typeof(x)='object' AND jsonb_typeof(cr)='object' AND x->>'id'=cr->>'id' LIMIT 1) l(rc) ON true)
   ELSE r.criterion_results END,
  'metrics',r.metrics,'completedAt',r.created_at)
 FROM sessions s JOIN session_results r ON r.session_id=s.id LEFT JOIN scenario_versions v ON v.id=s.scenario_version_id
 WHERE s.id=p_session AND s.trainee_user_id=p_actor AND s.contract_version=7
$$;

DO $grants$ DECLARE r text;s record;sig text;BEGIN
 FOREACH sig IN ARRAY ARRAY['ai_trainee_training_snapshot(uuid,uuid)','ai_trainee_result_snapshot(uuid,uuid)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 SELECT * INTO s FROM ai_trainee_criteria_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
