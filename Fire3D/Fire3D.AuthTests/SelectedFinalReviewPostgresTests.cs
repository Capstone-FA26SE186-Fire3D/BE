using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 private async Task<(Guid Revision,Guid Job,Guid Run)> FinalReviewGeometry(string outcome,object[] issues)
 {
  var revision=await SeedVerifiedIfcRevision();Guid job=default,run=default;
  await WithProcessingRuntime(async(api,worker)=>
  {
   await using var db=BuildingContext(api);
   var store=new ProcessingRuntimeStore(db,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build());
   var requested=await store.RequestAsync(adminId,revision,"geometry-review",default);Assert.True(requested.IsSuccess,requested.Error?.Code);job=requested.Value;
   var delivery=await store.ExecuteAsync("Claim",null,null,null,default);
   var claimed=await store.ExecuteAsync("Claim",job,WorkerInput(new{eventKey=delivery.GetProperty("eventKey").GetString(),payloadHash=delivery.GetProperty("payloadHash").GetString(),inputHash=IfcHash,toolchainVersion="fake-ifc-review"}),default);Assert.True(claimed.IsSuccess,claimed.Error?.Code);
   var attemptId=claimed.Value.GetProperty("attemptId").GetGuid();var leaseToken=claimed.Value.GetProperty("leaseToken").GetGuid();
   var output=new{artifactType="preview_glb",objectKey=claimed.Value.GetProperty("outputPrefix").GetString()+"preview.glb",sha256Hash=IfcHash,sizeBytes=100L,schemaVersion="1",metadata=new{objectAnchors=new[]{"door-1"}},validatorVersion="fake-review",outcome,issues};
   var registered=await store.ExecuteAsync("Output",job,WorkerInput(new{attemptId,leaseToken,output}),default);Assert.True(registered.IsSuccess,registered.Error?.Code);
   var completed=await store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId,leaseToken,outputHash=registered.Value.GetProperty("outputHash").GetString()}),default);Assert.True(completed.IsSuccess,completed.Error?.Code);run=completed.Value.GetProperty("validationRunId").GetGuid();
  });
  return(revision,job,run);
 }

 [PostgresFact]
 public async Task Final_review_failed_geometry_cannot_validate_or_snapshot_its_object_anchors()
 {
  var f=await FinalReviewGeometry("Failed",[]);
  Assert.Equal("Succeeded",await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{f.Job}'"));Assert.Equal("Failed",await ScalarAsync($"SELECT outcome FROM validation_runs WHERE id='{f.Run}'"));
  var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
  await WithAuthoringRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new ScenarioWriteStore(db);var read=new ScenarioReadStore(db);
   var scenario=(await store.CreateScenarioAsync(adminId,building,null,new(building,"Failed geometry"),default,"scenario")).Value;
   var draft=(await store.CreateScenarioDraftAsync(adminId,scenario,null,new(f.Revision),default,"draft")).Value;
   var state=ValidAuthoringState() with{ObjectAnchors=["door-1"]};var revision=Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
   var updated=await store.UpdateScenarioDraftAsync(adminId,draft,revision,state,null,default);Assert.True(updated.IsSuccess,updated.Error?.Code);
   Assert.Contains(await read.ValidateReferencesAsync(f.Revision,JsonSerializer.SerializeToNode(state,WorkerJson)!,default),x=>x.Code=="ANCHOR_NOT_FOUND");
   var snapshot=await store.SnapshotScenarioDraftAsync(adminId,draft,null,default,"snapshot",updated.Value);Assert.Equal("VALIDATION_ERROR",snapshot.Error?.Code);
   Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM scenario_versions"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM authoring_command_receipts WHERE operation='Snapshot'"));
   client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
   var validation=await client.PostAsync($"/api/scenario-drafts/{draft}/validate",null);Assert.Equal(HttpStatusCode.OK,validation.StatusCode);Assert.Contains("ANCHOR_NOT_FOUND",await validation.Content.ReadAsStringAsync());
  });
 }

 [PostgresFact]
 public async Task Final_review_geometry_anchor_requires_passed_current_attempt_without_blockers()
 {
  var f=await FinalReviewGeometry("Passed",[]);
  await WithAuthoringRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var read=new ScenarioReadStore(db);var state=JsonSerializer.SerializeToNode(ValidAuthoringState() with{ObjectAnchors=["door-1"]},WorkerJson)!;
   Assert.Empty(await read.ValidateReferencesAsync(f.Revision,state,default));
   await ExecuteAsync($"INSERT INTO revision_issues(id,validation_run_id,severity,code,message,details,created_at) VALUES(gen_random_uuid(),'{f.Run}','Warning','LEGACY_WARNING','Warning fixture','{{}}',now())");
   Assert.Empty(await read.ValidateReferencesAsync(f.Revision,state,default));
   foreach(var severity in new[]{"Error","Critical"})
   {
    await ExecuteAsync($"UPDATE revision_issues SET severity='{severity}' WHERE validation_run_id='{f.Run}'");
    Assert.Contains(await read.ValidateReferencesAsync(f.Revision,state,default),x=>x.Code=="ANCHOR_NOT_FOUND");
   }
   await ExecuteAsync($"DELETE FROM revision_issues WHERE validation_run_id='{f.Run}';UPDATE processing_jobs SET current_attempt_id=NULL WHERE id='{f.Job}'");
   Assert.Contains(await read.ValidateReferencesAsync(f.Revision,state,default),x=>x.Code=="ANCHOR_NOT_FOUND");
  });
 }
}
