using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Xunit;
using Npgsql;
using Fire3D.Application.Releases;
using Fire3D.Infrastructure.Releases;
using Fire3D.Infrastructure.Buildings;
using Fire3D.Application.Buildings;
using Fire3D.Infrastructure.Persistence;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 [PostgresFact]
 public async Task Release_build_requires_content_approval_even_with_confirmed_technical_pair()
 {
  var f=await SeedReadyPackage();await using var db=BuildingContext(testConnection);var readiness=new ScenarioReadinessStore(db);
  var confirmed=await readiness.ExecuteAsync("Confirm",f.Owner,f.Version,f.Revision,new ConfirmTrainingRequest(f.Version,f.Run),null,default);Assert.True(confirmed.IsSuccess,confirmed.Error?.Code);
  await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;var tokens=await LoginAsync(email);client.DefaultRequestHeaders.Authorization=new("Bearer",tokens.AccessToken);client.DefaultRequestHeaders.Add("Idempotency-Key","release-first");
  var response=await client.PostAsJsonAsync("/api/releases",new{revisionId=f.Revision,scenarioVersionId=f.Version,confirmationReviewId=confirmed.Value.GetProperty("reviewId").GetGuid(),candidateArtifactId=f.Artifact,safetyThresholds="{}",manifestUrl="untrusted/manifest.json",manifestSha256=new string('a',64),packageUrl="untrusted/package.zip",checksumSha256=new string('b',64),packageSizeBytes=100,minRuntimeVersion="1.0.0",schemaVersion="1",buildTarget="Windows"});
  Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("CONTENT_APPROVAL_REQUIRED",await response.Content.ReadAsStringAsync());Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM releases"));
 }

 private async Task WithReleaseRuntime(Func<string,Task> action)
 {
  var login="release_test_"+Guid.NewGuid().ToString("N");
  var credentials=new NpgsqlConnectionStringBuilder(testConnection);
  await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{(credentials.Password??"").Replace("'","''")}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint),publish_release_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO {login}");
  try{await action(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Pooling=false}.ConnectionString);}finally{await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
 }
 private async Task<BuildReleaseRequest> ApproveRelease((Guid Owner,Guid Revision,Guid Version,Guid Run,Guid Artifact) f)
 {
  await using var db=BuildingContext(testConnection);var readiness=new ScenarioReadinessStore(db);
  var confirm=await readiness.ExecuteAsync("Confirm",f.Owner,f.Version,f.Revision,new ConfirmTrainingRequest(f.Version,f.Run),null,default);Assert.True(confirm.IsSuccess,confirm.Error?.Code);
  var submitted=await readiness.ExecuteAsync("Submit",f.Owner,f.Version,null,new{},"submit",default);Assert.True(submitted.IsSuccess,submitted.Error?.Code);
  var approval=await readiness.ExecuteAsync("Approve",adminId,f.Version,null,new{contentHash=submitted.Value.GetProperty("contentHash").GetString(),rubricHash=submitted.Value.GetProperty("rubricHash").GetString()},"approve",default);Assert.True(approval.IsSuccess,approval.Error?.Code);
  return new(f.Revision,f.Version,confirm.Value.GetProperty("reviewId").GetGuid(),f.Artifact);
 }
 [PostgresFact]
 public async Task Release_gate_derives_package_builds_training_once_and_rolls_back_audit_failure()
 {
  var f=await SeedReadyPackage();var input=await ApproveRelease(f);var family=await SeedPlaytestFamily(f.Owner);
  await WithReleaseRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);await using var db2=BuildingContext(runtime);var store=new ReleaseWriteStore(db,TimeProvider.System);var second=new ReleaseWriteStore(db2,TimeProvider.System);
   Assert.Equal("PACKAGE_METADATA_MISMATCH",(await store.BuildAsync(f.Owner,null,input with{PackageUrl="client-key"},default,"wrong",family)).Error?.Code);
   await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT release_audit_fault CHECK(target_entity<>'Release') NOT VALID");
   await Assert.ThrowsAsync<PostgresException>(()=>store.BuildAsync(f.Owner,null,input,default,"build",family));
   Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM releases"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM trainings"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM release_command_receipts"));
   await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT release_audit_fault");
   var builds=await Task.WhenAll(store.BuildAsync(f.Owner,null,input,default,"build",family),second.BuildAsync(f.Owner,null,input,default,"build",family));Assert.All(builds,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(builds[0].Value!.Id,builds[1].Value!.Id);
   Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM releases"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM trainings"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM release_build_provenance"));
   Assert.Equal(await ScalarAsync($"SELECT object_key FROM revision_artifacts WHERE id='{f.Artifact}'"),builds[0].Value!.Package.PackageUrl);
   Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await store.BuildAsync(f.Owner,null,input with{CandidateArtifactId=Guid.NewGuid()},default,"build",family)).Error?.Code);
   Assert.Equal("RELEASE_ALREADY_EXISTS",(await store.BuildAsync(f.Owner,null,input,default,"another",family)).Error?.Code);
   await using var con=new NpgsqlConnection(runtime);await con.OpenAsync();await Assert.ThrowsAsync<PostgresException>(()=>new NpgsqlCommand("UPDATE releases SET status='Published'",con).ExecuteNonQueryAsync());
   Assert.Equal("PUBLISH_GATE_UNAVAILABLE",(await store.PublishAsync(f.Owner,builds[0].Value!.Id,null,default)).Error?.Code);
   Assert.True((await store.RevokeAsync(f.Owner,builds[0].Value!.Id,null,"Fixture revoke",default,family)).IsSuccess);
   Assert.True((await store.RevokeAsync(f.Owner,builds[0].Value!.Id,null,"Fixture revoke",default,family)).IsSuccess);
   Assert.Equal(2L,await ScalarAsync("SELECT count(*) FROM audit_logs WHERE target_entity='Release'"));
  });
 }
 [PostgresFact]
 public async Task Building_participation_is_account_bound_and_revision_changes_revoke_old_access()
 {
  var f=await SeedReadyPackage();var request=await ApproveRelease(f);var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
  var trainee=Guid.NewGuid();var other=Guid.NewGuid();await ExecuteAsync($"INSERT INTO users(id,email,username,role,is_active,created_at,updated_at) VALUES('{trainee}','t-{trainee:N}@example.test','t_'||left(md5(gen_random_uuid()::text),20),'Trainee',true,now(),now()),('{other}','t-{other:N}@example.test','t_'||left(md5(gen_random_uuid()::text),20),'Trainee',true,now(),now())");var tf=await SeedPlaytestFamily(trainee);var of=await SeedPlaytestFamily(other);
  await WithReleaseRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var service=new BuildingAccessService(db);var list=new TrainingReadStore(db);var release=new ReleaseWriteStore(db,TimeProvider.System);
   var built=await release.BuildAsync(f.Owner,null,request,default,"build",family);Assert.True(built.IsSuccess,built.Error?.Code);
   Assert.Equal("BUILDING_ACCESS_REQUIRED",(await list.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Error?.Code);
   var access=await service.Execute("GetAccess",f.Owner,family,building,new{},null,default);Assert.Equal("Private",access.Value.GetProperty("visibility").GetString());
   Assert.Equal("PRECONDITION_REQUIRED",(await service.Execute("Access",f.Owner,family,building,new BuildingAccessRequest("Public"),null,default)).Error?.Code);
   var rotated=await service.Execute("Rotate",f.Owner,family,building,new{},1,default);Assert.True(rotated.IsSuccess,rotated.Error?.Code);var code=rotated.Value.GetProperty("code").GetString()!;Assert.DoesNotContain(code,(string)(await ScalarAsync($"SELECT participation_code_hash FROM buildings WHERE id='{building}'"))!);
   Assert.True((await service.Execute("Verify",trainee,tf,building,new ParticipationCodeRequest(code),null,default)).IsSuccess);
   Assert.Empty((await list.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Value!); // Built is not Published.
   await ExecuteAsync($"UPDATE releases SET status='Published' WHERE id='{built.Value!.Id}'"); // explicit fixture, no runtime publish capability.
   Assert.Single((await list.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Value!);
   Assert.Equal("BUILDING_ACCESS_REQUIRED",(await list.GetTrainingsByBuildingAsync(other,building,null,default,of)).Error?.Code);
   Assert.Equal("PRECONDITION_FAILED",(await service.Execute("RevokeCode",f.Owner,family,building,new{},1,default)).Error?.Code);
   Assert.True((await service.Execute("RevokeCode",f.Owner,family,building,new{},2,default)).IsSuccess);
   Assert.Equal("BUILDING_ACCESS_REQUIRED",(await list.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Error?.Code);
   Assert.Equal("PARTICIPATION_CODE_INVALID",(await service.Execute("Verify",trainee,tf,building,new ParticipationCodeRequest(code),null,default)).Error?.Code);
   Assert.True((await service.Execute("Access",f.Owner,family,building,new BuildingAccessRequest("Public"),3,default)).IsSuccess);
   Assert.Single((await list.GetTrainingsByBuildingAsync(other,building,null,default,of)).Value!);
   await ExecuteAsync($"UPDATE organizations SET is_active=false WHERE id=(SELECT organization_id FROM buildings WHERE id='{building}')");
   Assert.Equal("NOT_FOUND",(await list.GetTrainingsByBuildingAsync(other,building,null,default,of)).Error?.Code);
  });
 }
}

