using System.Net;
using System.Net.Http.Json;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.Json;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 [PostgresFact]
 public async Task Playtest_HTTP_admin_is_not_a_playtest_actor()
 {
  client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
  var prepare=await client.PostAsJsonAsync($"/api/scenarios/{Guid.NewGuid()}/playtests?buildingId={Guid.NewGuid()}",new{revisionId=Guid.NewGuid(),scenarioVersionId=Guid.NewGuid()});
  Assert.Equal(HttpStatusCode.Forbidden,prepare.StatusCode);
  var start=await client.PostAsJsonAsync($"/api/playtests/{Guid.NewGuid()}/start",new{runtimeVersion="1.0.0"});
  Assert.Equal(HttpStatusCode.Forbidden,start.StatusCode);
 }
 private static readonly PlaytestOptions PlaytestFixtureOptions=new(){Enabled=true,SigningKey="fixture-only-playtest-key-32-bytes-never-production"};
 private async Task<Guid> SeedPlaytestFamily(Guid owner)
 {
  var family=Guid.NewGuid();await ExecuteAsync($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{owner}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour'); INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities) VALUES(gen_random_uuid(),'1.0.0','1','1','[]') ON CONFLICT DO NOTHING");return family;
 }
 private async Task<Guid> SeedTrial(Guid owner,Guid building,int units)
 {
  var package=Guid.NewGuid();var entitlement=Guid.NewGuid();await ExecuteAsync($"INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES('{package}','fixture-{package:N}','Explicit Trial fixture',100,'VND',1,'{{}}',true,'{adminId}',now(),now()); INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,provisioning_key,status,starts_at,ends_at,playtest_units_granted,created_by) SELECT '{entitlement}',organization_id,id,'{package}','fixture:{entitlement}','Trial',now()-interval '1 minute',now()+interval '1 day',{units},'{owner}' FROM buildings WHERE id='{building}'");return entitlement;
 }
 private async Task WithPlaytestRuntime(Func<string,Task> action)
 {
  var login="playtest_test_"+Guid.NewGuid().ToString("N");await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION playtest_lifecycle_gate(text,uuid,uuid,uuid,uuid,jsonb,text) TO {login}");
  try{await action(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Pooling=false}.ConnectionString);}finally{await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
 }
 [PostgresFact]
 public async Task Playtest_prepare_pins_immutable_draft_start_consumes_trial_once_and_returns_runtime_grant()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var draft=(Guid)(await ScalarAsync($"SELECT id FROM scenario_drafts WHERE scenario_id='{scenario}'"))!;
  var trial=await SeedTrial(f.Owner,building,1);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);await using var otherDb=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var other=new PlaytestLifecycle(otherDb,Options.Create(PlaytestFixtureOptions));
   var input=new PreparePlaytestRequest(f.Revision,ScenarioDraftId:draft);
   var prepared=await store.PrepareAsync(f.Owner,family,building,scenario,input,"prepare-one",default);Assert.True(prepared.IsSuccess,prepared.Error?.Code);Assert.Equal(f.Version,prepared.Value!.ScenarioVersionId);Assert.Equal(f.Artifact,prepared.Value.ArtifactId);
   Assert.Equal(0,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));Assert.Equal(true,await ScalarAsync($"SELECT service_entitlement_id IS NULL FROM playtest_sessions WHERE id='{prepared.Value.Id}'"));
   var replay=await store.PrepareAsync(f.Owner,family,null,scenario,input,"prepare-one",default);Assert.Equal(prepared.Value.Id,replay.Value!.Id);
   Assert.Equal("PACKAGE_METADATA_MISMATCH",(await store.PrepareAsync(f.Owner,family,building,scenario,input with{PackageHash=new string('b',64)},"bad-meta",default)).Error?.Code);
   Assert.Equal("RUNTIME_INCOMPATIBLE",(await store.StartAsync(f.Owner,family,prepared.Value.Id,new("2.0.0"),"bad-runtime",default)).Error?.Code);
   var launched=await Task.WhenAll(store.StartAsync(f.Owner,family,prepared.Value.Id,new("1.0.0"),"start-one",default),other.StartAsync(f.Owner,family,prepared.Value.Id,new("1.0.0"),"start-one",default));Assert.All(launched,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(launched[0].Value!.LaunchGrant,launched[1].Value!.LaunchGrant);Assert.Equal(1,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));
   var grant=launched[0].Value!;Assert.Equal(TimeSpan.FromMinutes(5),grant.ExpiresAt-grant.IssuedAt);
   var principal=new JwtSecurityTokenHandler().ValidateToken(grant.LaunchGrant,new TokenValidationParameters{ValidateIssuer=true,ValidIssuer=PlaytestFixtureOptions.Issuer,ValidateAudience=true,ValidAudience=PlaytestFixtureOptions.Audience,ValidateLifetime=true,ClockSkew=TimeSpan.Zero,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PlaytestFixtureOptions.SigningKey))},out _);
   Assert.Equal("playtest",principal.FindFirstValue("purpose"));Assert.Equal(f.Version.ToString(),principal.FindFirstValue("scenario_version_id"));Assert.Equal(family.ToString(),principal.FindFirstValue("sid"));Assert.Equal(grant.PackageHash,principal.FindFirstValue("package_hash"));
   client.DefaultRequestHeaders.Authorization=new("Bearer",grant.LaunchGrant);Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/auth/me")).StatusCode);
   Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await store.StartAsync(f.Owner,family,prepared.Value.Id,new("2.0.0"),"start-one",default)).Error?.Code);
   Assert.Equal("PLAYTEST_ALREADY_STARTED",(await store.StartAsync(f.Owner,family,prepared.Value.Id,new("1.0.0"),"new-start",default)).Error?.Code);
   var second=await store.PrepareAsync(f.Owner,family,building,scenario,input,"prepare-two",default);Assert.True(second.IsSuccess);Assert.Equal("PLAYTEST_ENTITLEMENT_REQUIRED",(await store.StartAsync(f.Owner,family,second.Value!.Id,new("1.0.0"),"exhausted",default)).Error?.Code);
   await Assert.ThrowsAsync<PostgresException>(async()=>{await using var con=new NpgsqlConnection(runtime);await con.OpenAsync();await new NpgsqlCommand($"UPDATE service_entitlements SET playtest_units_used=0 WHERE id='{trial}'",con).ExecuteNonQueryAsync();});
   await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{family}'");Assert.Equal("UNAUTHORIZED",(await store.StartAsync(f.Owner,family,prepared.Value.Id,new("1.0.0"),"start-one",default)).Error?.Code);
  });
 }
 [PostgresFact]
 public async Task Playtest_start_audit_failure_rolls_back_trial_session_and_receipt()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var trial=await SeedTrial(f.Owner,building,1);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var prepared=await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"prepare",default);Assert.True(prepared.IsSuccess,prepared.Error?.Code);var id=prepared.Value!.Id;
   await ExecuteAsync($"ALTER TABLE audit_logs ADD CONSTRAINT playtest_audit_fault CHECK(target_id<>'{id}'::uuid) NOT VALID");await Assert.ThrowsAsync<PostgresException>(()=>store.StartAsync(f.Owner,family,id,new("1.0.0"),"start",default));Assert.Equal(0,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));Assert.Equal("Created",await ScalarAsync($"SELECT status FROM playtest_sessions WHERE id='{id}'"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_command_receipts WHERE operation='Start'"));
   await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT playtest_audit_fault");Assert.True((await store.StartAsync(f.Owner,family,id,new("1.0.0"),"start",default)).IsSuccess);
  });
 }

 [PostgresFact]
 public async Task Playtest_two_sessions_race_for_last_trial_unit_and_foreign_owner_is_denied()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var trial=await SeedTrial(f.Owner,building,1);
  var stranger=Guid.NewGuid();await ExecuteAsync($"INSERT INTO users(id,email,role,organization_id,is_active,created_at,updated_at) SELECT '{stranger}','stranger-{stranger:N}@example.test','OrganizationUser',organization_id,true,now(),now() FROM users WHERE id='{f.Owner}'");var strangerFamily=await SeedPlaytestFamily(stranger);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);await using var otherDb=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var other=new PlaytestLifecycle(otherDb,Options.Create(PlaytestFixtureOptions));var input=new PreparePlaytestRequest(f.Revision,ScenarioVersionId:f.Version);
   var first=await store.PrepareAsync(f.Owner,family,null,scenario,input,"first",default);var second=await store.PrepareAsync(f.Owner,family,null,scenario,input,"second",default);Assert.True(first.IsSuccess);Assert.True(second.IsSuccess);
   // Header keys are actor-scoped even when legacy columns have global unique indexes.
   Assert.True((await other.PrepareAsync(stranger,strangerFamily,null,scenario,input,"first",default)).IsSuccess);
   Assert.Equal("NOT_FOUND",(await other.StartAsync(stranger,strangerFamily,first.Value!.Id,new("1.0.0"),"foreign",default)).Error?.Code);
   var results=await Task.WhenAll(store.StartAsync(f.Owner,family,first.Value!.Id,new("1.0.0"),"start-first",default),other.StartAsync(f.Owner,family,second.Value!.Id,new("1.0.0"),"start-second",default));Assert.Single(results,x=>x.IsSuccess);Assert.Single(results,x=>x.Error?.Code=="PLAYTEST_ENTITLEMENT_REQUIRED");Assert.Equal(1,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));
   await ExecuteAsync("UPDATE playtest_command_receipts SET result=jsonb_set(result,'{expiresAt}',to_jsonb(now()-interval '1 second')) WHERE operation='Start'");var winner=results[0].IsSuccess?first.Value!.Id:second.Value!.Id;var key=results[0].IsSuccess?"start-first":"start-second";
   Assert.Equal("PLAYTEST_GRANT_EXPIRED",(await store.StartAsync(f.Owner,family,winner,new("1.0.0"),key,default)).Error?.Code);
  });
 }
 [PostgresFact]
 public async Task Playtest_paid_building_service_does_not_consume_trial_units()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var org=(Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{f.Revision}'"))!;var trial=await SeedTrial(f.Owner,building,1);
  var package=Guid.NewGuid();var quote=Guid.NewGuid();var item=Guid.NewGuid();
  // Trusted fake-provider fixture through the foundation ledger gate, not an actual PayOS event.
  await ExecuteAsync($$"""
   INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{{building}}','Fixture address',now(),now()) ON CONFLICT (building_id) DO UPDATE SET address='Fixture address';
   INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES('{{package}}','paid-{{package:N}}','Paid fixture',100,'VND',1,'{}',true,'{{adminId}}',now(),now());
   INSERT INTO quotations(id,organization_id,requested_by,quotation_number,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at) VALUES('{{quote}}','{{org}}','{{f.Owner}}','paid-{{quote:N}}',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
   INSERT INTO quotation_building_items(id,quotation_id,building_id,service_package_id,purchase_action,service_duration_months,unit_price,discount_amount,subtotal_amount,total_amount,currency,price_snapshot,terms_snapshot,discount_snapshot,line_provisioning_key,created_at,updated_at) VALUES('{{item}}','{{quote}}','{{building}}','{{package}}','New',1,100,0,100,100,'VND','{}','{}','{}','line:{{item}}',now(),now());
   UPDATE quotations SET status='Issued',issued_by='{{adminId}}',issued_at=now() WHERE id='{{quote}}';UPDATE quotations SET status='Accepted' WHERE id='{{quote}}';
   SELECT create_pending_payos_payment_request('{{quote}}','{{f.Owner}}','fixture-paid',123456,'https://pay.payos.vn/fixture','https://fet3d.io.vn/return','https://fet3d.io.vn/cancel',now()+interval '1 hour');
   SELECT apply_verified_payos_webhook((SELECT id FROM payos_payment_requests WHERE quotation_id='{{quote}}'),'fake-paid','fake-paid-ref',123456,100,'VND','{}');
   """);
  var tx=(Guid)(await ScalarAsync("SELECT id FROM payment_transactions WHERE provider_transaction_id='fake-paid-ref' AND status='Applied'"))!;
  await ExecuteAsync($$"""
   INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,quotation_item_id,payment_transaction_id,provisioning_key,status,starts_at,ends_at,price_snapshot,terms_snapshot,created_by) VALUES(gen_random_uuid(),'{{org}}','{{building}}','{{package}}','{{quote}}','{{item}}','{{tx}}','service:{{item}}:{{tx}}','Active',now()-interval '30 seconds',now()+interval '1 day','{}','{}','{{f.Owner}}');
   """);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var prepare=await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"paid-prepare",default);Assert.True(prepare.IsSuccess,prepare.Error?.Code);var started=await store.StartAsync(f.Owner,family,prepare.Value!.Id,new("1.0.0"),"paid-start",default);Assert.True(started.IsSuccess,started.Error?.Code);Assert.Equal(0,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));Assert.Equal("Active",await ScalarAsync($"SELECT e.status FROM playtest_sessions p JOIN service_entitlements e ON e.id=p.service_entitlement_id WHERE p.id='{prepare.Value.Id}'"));
  });
 }

 [PostgresFact]
 public async Task Playtest_HTTP_contract_validation_package_and_launch_response_match_OpenAPI()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
  await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;var tokens=await LoginAsync(email);
  using var enabled=factory!.WithWebHostBuilder(builder=>builder.ConfigureAppConfiguration((_,configuration)=>configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Playtest:Enabled"]="true",["Playtest:SigningKey"]=PlaytestFixtureOptions.SigningKey})));using var browser=enabled.CreateClient();browser.DefaultRequestHeaders.Authorization=new("Bearer",tokens.AccessToken);
  var path=$"/api/scenarios/{scenario}/playtests";
  var bad=await browser.PostAsJsonAsync(path,new{revisionId=f.Revision,scenarioDraftId=Guid.NewGuid(),scenarioVersionId=f.Version});Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode);Assert.Contains("scenarioVersionId",await bad.Content.ReadAsStringAsync());
  Assert.Equal(HttpStatusCode.BadRequest,(await browser.PostAsJsonAsync(path,new{revisionId=f.Revision,scenarioVersionId=f.Version})).StatusCode);
  browser.DefaultRequestHeaders.Add("Idempotency-Key","http-prepare");var prepare=await browser.PostAsJsonAsync(path,new{revisionId=f.Revision,scenarioVersionId=f.Version});Assert.True(prepare.StatusCode==HttpStatusCode.Created,await prepare.Content.ReadAsStringAsync());var pin=(await prepare.Content.ReadFromJsonAsync<PlaytestPreparation>())!;Assert.Equal(f.Version,pin.ScenarioVersionId);
  browser.DefaultRequestHeaders.Remove("Idempotency-Key");browser.DefaultRequestHeaders.Add("Idempotency-Key","http-start");var startPath=$"/api/playtests/{pin.Id}/start";
  var invalid=await browser.PostAsJsonAsync(startPath,new{runtimeVersion="1.0"});Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);Assert.Contains("runtimeVersion",await invalid.Content.ReadAsStringAsync());
  var denied=await browser.PostAsJsonAsync(startPath,new{runtimeVersion="1.0.0"});Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);Assert.Contains("PLAYTEST_ENTITLEMENT_REQUIRED",await denied.Content.ReadAsStringAsync());
  await SeedTrial(f.Owner,building,1);var started=await browser.PostAsJsonAsync(startPath,new{runtimeVersion="1.0.0"});Assert.True(started.StatusCode==HttpStatusCode.OK,await started.Content.ReadAsStringAsync());var launch=(await started.Content.ReadFromJsonAsync<PlaytestLaunch>())!;Assert.NotEmpty(launch.LaunchGrant);Assert.Equal(f.Version,launch.ScenarioVersionId);
  using var doc=JsonDocument.Parse(await browser.GetStringAsync("/openapi/v1.json"));var paths=doc.RootElement.GetProperty("paths");var operation=paths.GetProperty("/api/playtests/{playtestId}/start").GetProperty("post");Assert.Contains(operation.GetProperty("parameters").EnumerateArray(),p=>p.GetProperty("name").GetString()=="Idempotency-Key"&&p.GetProperty("required").GetBoolean());Assert.True(operation.GetProperty("responses").GetProperty("200").GetProperty("content").EnumerateObject().Any());
 }
 [PostgresFact]
 public async Task Playtest_start_rechecks_expiry_after_waiting_for_building_lock()
 {
  var f=await SeedReadyPackage("PlaytestPackage");var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var trial=await SeedTrial(f.Owner,building,1);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var pin=await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"prepare",default);Assert.True(pin.IsSuccess);
   await using var blocker=new NpgsqlConnection(testConnection);await blocker.OpenAsync();await using var transaction=await blocker.BeginTransactionAsync();await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:building:{building}',0))",blocker,transaction).ExecuteNonQueryAsync();
   var start=store.StartAsync(f.Owner,family,pin.Value!.Id,new("1.0.0"),"start",default);
   var login=new NpgsqlConnectionStringBuilder(runtime).Username;bool waiting=false;
   for(var attempt=0;attempt<100;attempt++){waiting=(bool)(await ScalarAsync($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE usename='{login}' AND wait_event='advisory')"))!;if(waiting)break;await Task.Delay(20);}Assert.True(waiting,"Start must be blocked after its initial session check.");
   // Advancing the stored expiry represents time elapsing while the request waits.
   await ExecuteAsync($"UPDATE auth_refresh_tokens SET expires_at=now()-interval '1 second' WHERE family_id='{family}'");await transaction.CommitAsync();Assert.Equal("UNAUTHORIZED",(await start).Error?.Code);Assert.Equal(0,await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{trial}'"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_command_receipts WHERE operation='Start'"));
  });
 }

 [PostgresFact]
 public async Task Playtest_requires_current_draft_snapshot_runtime_capabilities_and_entitlement_for_exact_building()
 {
  var f=await SeedReadyPackage("PlaytestPackage",minRuntimeVersion:"1.1.0",requiredCapabilities:["smoke"]);var family=await SeedPlaytestFamily(f.Owner);var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;var draft=(Guid)(await ScalarAsync($"SELECT id FROM scenario_drafts WHERE scenario_id='{scenario}'"))!;
  var foreignBuilding=Guid.NewGuid();await ExecuteAsync($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) SELECT '{foreignBuilding}',organization_id,'Other Building',true,'{f.Owner}',now(),now() FROM buildings WHERE id='{building}';UPDATE scenario_drafts SET state=jsonb_set(state,'{{learnerInstructions}}','\"Changed instructions\"'::jsonb),updated_at=now() WHERE id='{draft}'");await SeedTrial(f.Owner,foreignBuilding,1);
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));Assert.Equal("DRAFT_SNAPSHOT_REQUIRED",(await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioDraftId:draft),"stale-draft",default)).Error?.Code);
   var prepare=await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"version",default);Assert.True(prepare.IsSuccess,prepare.Error?.Code);
   Assert.Equal("RUNTIME_INCOMPATIBLE",(await store.StartAsync(f.Owner,family,prepare.Value!.Id,new("1.0.0"),"runtime",default)).Error?.Code);
   await ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities) VALUES(gen_random_uuid(),'1.1.0','1','1','[]')");Assert.Equal("RUNTIME_INCOMPATIBLE",(await store.StartAsync(f.Owner,family,prepare.Value!.Id,new("1.1.0"),"runtime",default)).Error?.Code);
   await ExecuteAsync("UPDATE runtime_compatibility_catalog SET capabilities='[\"smoke\"]'::jsonb WHERE runtime_version='1.1.0'");Assert.Equal("PLAYTEST_ENTITLEMENT_REQUIRED",(await store.StartAsync(f.Owner,family,prepare.Value!.Id,new("1.1.0"),"runtime",default)).Error?.Code);
   await SeedTrial(f.Owner,building,1);Assert.True((await store.StartAsync(f.Owner,family,prepare.Value!.Id,new("1.1.0"),"runtime",default)).IsSuccess);
  });
 }

 [PostgresFact]
 public async Task Playtest_rejects_inconsistent_accepted_manifest_metadata_without_preparation()
 {
  var f=await SeedReadyPackage("PlaytestPackage",manifestProtocolVersion:"different-protocol");var family=await SeedPlaytestFamily(f.Owner);var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));Assert.Equal("PACKAGE_METADATA_MISMATCH",(await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"prepare",default)).Error?.Code);Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_sessions"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_command_receipts"));
  });
 }

 [PostgresFact]
 public async Task Playtest_runtime_metadata_fails_closed_without_cast_errors()
 {
  var f=await SeedReadyPackage("PlaytestPackage",minRuntimeVersion:"01.0.0");var family=await SeedPlaytestFamily(f.Owner);var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
  await WithPlaytestRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var store=new PlaytestLifecycle(db,Options.Create(PlaytestFixtureOptions));var prepare=await store.PrepareAsync(f.Owner,family,null,scenario,new(f.Revision,ScenarioVersionId:f.Version),"prepare",default);Assert.Equal("PACKAGE_METADATA_MISMATCH",prepare.Error?.Code);
   Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_sessions"));
   Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM playtest_command_receipts WHERE operation='Start'"));
  });
 }

}
