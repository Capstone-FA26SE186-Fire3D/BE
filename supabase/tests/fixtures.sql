-- Synthetic metadata only. No claim that these placeholder hashes identify real IFC/packages.
CREATE FUNCTION pg_temp.tid(n INT) RETURNS UUID LANGUAGE sql IMMUTABLE AS $$ SELECT md5(n::text)::uuid $$;
INSERT INTO organizations(id,name,slug) VALUES(pg_temp.tid(1),'Organization A','org-a'),(pg_temp.tid(2),'Organization B','org-b');
INSERT INTO users(id,organization_id,email,password_hash,role) VALUES
 (pg_temp.tid(1),pg_temp.tid(1),'author-a@example.test','fixture-only-not-a-real-password-hash','OrganizationUser'),
 (pg_temp.tid(2),pg_temp.tid(2),'author-b@example.test','fixture-only-not-a-real-password-hash','OrganizationUser'),
 (pg_temp.tid(3),NULL,'learner-a@example.test','fixture-only-not-a-real-password-hash','Trainee'),
 (pg_temp.tid(4),NULL,'learner-b@example.test','fixture-only-not-a-real-password-hash','Trainee'),
 (pg_temp.tid(5),pg_temp.tid(1),'author-a2@example.test','fixture-only-not-a-real-password-hash','OrganizationUser'),
 (pg_temp.tid(6),NULL,'admin@example.test','fixture-only-not-a-real-password-hash','PlatformAdmin');
INSERT INTO user_devices(id,user_id,device_uuid) VALUES(pg_temp.tid(1),pg_temp.tid(3),'shared-install'),(pg_temp.tid(2),pg_temp.tid(4),'shared-install');
INSERT INTO buildings(id,organization_id,name,created_by) VALUES(pg_temp.tid(1),pg_temp.tid(1),'Building A',pg_temp.tid(1)),(pg_temp.tid(2),pg_temp.tid(2),'Building B',pg_temp.tid(2));
INSERT INTO building_floors(id,building_id,organization_id,floor_number,floor_name) VALUES
 (pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),0,'Ground'),(pg_temp.tid(2),pg_temp.tid(1),pg_temp.tid(1),1,'Floor 1');
INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label) VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'R1');
INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,quarantine_status)
 VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'fixture.ifc',1000,'private/fixture.ifc','application/x-step',repeat('a',64),'Synthetic test fixture','Accepted');
UPDATE revisions SET status='Uploaded' WHERE id=pg_temp.tid(1);
INSERT INTO processing_jobs(id,revision_id,source_document_id,job_key,toolchain_version) VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'test-toolchain-1');
UPDATE revisions SET status='Processing' WHERE id=pg_temp.tid(1);
UPDATE processing_jobs SET status='Running',started_at=clock_timestamp(),lease_owner='test-worker',heartbeat_at=clock_timestamp() WHERE id=pg_temp.tid(1);
INSERT INTO revision_floors(id,revision_id,building_id,organization_id,building_floor_id,ifc_guid,floor_number,floor_name,elevation_meters,coordinate_transform)
 VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'ifc-ground',0,'Ground at R1',0,'{"unit":"meter"}'),
 (pg_temp.tid(2),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(2),'ifc-floor1',1,'Floor 1 at R1',3,'{"unit":"meter"}');
INSERT INTO revision_artifacts(id,revision_id,job_id,artifact_type,object_key,sha256_hash,size_bytes,schema_version)
 VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'Geometry','geometry.glb',repeat('1',64),100,'1.0'),
 (pg_temp.tid(2),pg_temp.tid(1),pg_temp.tid(1),'Graph','graph.json',repeat('2',64),100,'1.0'),
 (pg_temp.tid(3),pg_temp.tid(1),pg_temp.tid(1),'HazardGrid','grid.json',repeat('3',64),100,'1.0');
INSERT INTO validation_runs(id,revision_id,job_id,kind,outcome,validator_version,report)
 VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'Geometry','Passed','test-validator','{"connectivity":"passed"}');
UPDATE processing_jobs SET status='Succeeded',finished_at=clock_timestamp() WHERE id=pg_temp.tid(1);
UPDATE revisions SET status='ReadyForScenario' WHERE id=pg_temp.tid(1);
INSERT INTO scenarios(id,building_id,organization_id,name,created_by) VALUES(pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),'Scenario A',pg_temp.tid(1));
DO $$ DECLARE n INT; s UUID; BEGIN
 FOR n IN 1..2 LOOP
   INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,algorithm_version,random_seed,time_limit_seconds,
   spawn_config,goal_config,routing_config,scoring_config,mode_policy,created_by)
   VALUES(pg_temp.tid(n),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(1),n,'Scenario version '||n,'risk-astar-1',42,600,
   '{"nodeId":"room-1"}','{"exitIds":["exit-1"]}','{"hazardWeight":2,"portalWeight":1}',
   '{"rubricVersion":"1.0","wrongExitPenalty":10}',
   '{"Learn":{"guidance":"full"},"Guided":{"guidance":"full"},"Assessment":{"guidance":"none","debrief":"after-submit"}}',pg_temp.tid(1));
   INSERT INTO processing_jobs(id,revision_id,source_document_id,job_key,kind,scenario_version_id,toolchain_version)
   VALUES(pg_temp.tid(n+1),pg_temp.tid(1),pg_temp.tid(1),pg_temp.tid(n+1),'Scenario',pg_temp.tid(n),'builder-1');
   UPDATE processing_jobs SET status='Running',started_at=clock_timestamp(),lease_owner='test-builder',heartbeat_at=clock_timestamp() WHERE id=pg_temp.tid(n+1);
   INSERT INTO revision_artifacts(id,revision_id,job_id,artifact_type,object_key,sha256_hash,size_bytes,schema_version)
   VALUES(pg_temp.tid(n+3),pg_temp.tid(1),pg_temp.tid(n+1),'CandidatePackage','packages/'||n,repeat(n::text,64),100,'1.0');
   INSERT INTO validation_runs(id,revision_id,job_id,kind,scenario_version_id,candidate_artifact_id,outcome,scenario_hash,validator_version,report)
   SELECT pg_temp.tid(n+1),revision_id,pg_temp.tid(n+1),'Scenario',id,pg_temp.tid(n+3),'Passed',scenario_hash,'test-validator','{"route":"passed"}'
   FROM scenario_versions WHERE id=pg_temp.tid(n);
   UPDATE processing_jobs SET status='Succeeded',finished_at=clock_timestamp() WHERE id=pg_temp.tid(n+1);
   s:=confirm_revision_for_training(pg_temp.tid(1),pg_temp.tid(n),pg_temp.tid(1),pg_temp.tid(n+1),'Test readiness');
   INSERT INTO releases(id,revision_id,scenario_version_id,building_id,organization_id,confirmation_review_id)
   VALUES(pg_temp.tid(n),pg_temp.tid(1),pg_temp.tid(n),pg_temp.tid(1),pg_temp.tid(1),s);
   INSERT INTO release_packages(release_id,candidate_artifact_id,manifest_url,manifest_sha256,package_url,checksum_sha256,package_size_bytes,min_runtime_version)
   VALUES(pg_temp.tid(n),pg_temp.tid(n+3),'manifests/'||n,repeat('b',64),'packages/'||n,repeat(n::text,64),100,'1.0');
   INSERT INTO trainings(id,release_id,scenario_version_id,organization_id,name,status,created_by)
   VALUES(pg_temp.tid(n),pg_temp.tid(n),pg_temp.tid(n),pg_temp.tid(1),'Training '||n,'Active',pg_temp.tid(1));
   UPDATE releases SET status='Published',published_by=pg_temp.tid(1),published_at=clock_timestamp() WHERE id=pg_temp.tid(n);
   INSERT INTO release_qr_codes(id,release_id,training_id,organization_id,revision_floor_id,created_by,qr_hash)
   VALUES(pg_temp.tid(n),pg_temp.tid(n),pg_temp.tid(n),pg_temp.tid(1),pg_temp.tid(2),pg_temp.tid(1),repeat(n::text,64));
 END LOOP;
END $$;
