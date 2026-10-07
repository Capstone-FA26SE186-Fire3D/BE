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
 [PostgresFact]
 public async Task Final_review_ifc_gate_binds_legacy_receipt_to_building_even_when_payload_omits_resource()
 {
  var first=await SeedIfcBuilding();var second=await SeedIfcBuilding();
  await WithIfcRuntime(async runtime=>
  {
   await using var connection=new NpgsqlConnection(runtime);await connection.OpenAsync();
   async Task<JsonElement> Initiate(Guid building,string key="legacy-proof",NpgsqlConnection? other=null)
   {
    await using var cmd=new NpgsqlCommand("SELECT ifc_upload_gate('Initiate',@actor,@building,@input,@key,NULL,NULL)::text",other??connection);
    cmd.Parameters.AddWithValue("actor",adminId);cmd.Parameters.AddWithValue("building",building);
    cmd.Parameters.AddWithValue("key",key);
    cmd.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(new{fileSizeBytes=IfcBytes.Length,originalFilename="model.ifc",versionLabel=key,sha256Hash=IfcHash}));
    using var result=JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);return result.RootElement.Clone();
   }
   var created=await Initiate(first);Assert.Equal("OK",created.GetProperty("code").GetString());
   var replay=await Initiate(first);Assert.Equal(created.GetProperty("intent").GetProperty("revisionId").GetGuid(),replay.GetProperty("intent").GetProperty("revisionId").GetGuid());
   var conflict=await Initiate(second);Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",conflict.GetProperty("code").GetString());Assert.Equal(409,conflict.GetProperty("status").GetInt32());
   Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM ifc_upload_intents"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM revisions"));
   Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM ifc_object_cleanup"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM audit_logs WHERE target_entity='revisions'"));
   await using var other=new NpgsqlConnection(runtime);await other.OpenAsync();
   var race=await Task.WhenAll(Initiate(first,"concurrent-proof"),Initiate(second,"concurrent-proof",other));
   Assert.Single(race,x=>x.GetProperty("code").GetString()=="OK");Assert.Single(race,x=>x.GetProperty("code").GetString()=="IDEMPOTENCY_KEY_CONFLICT");
   Assert.Equal(2L,await ScalarAsync("SELECT count(*) FROM ifc_upload_intents"));Assert.Equal(2L,await ScalarAsync("SELECT count(*) FROM revisions"));
  });
 }

 [PostgresFact]
 public async Task Final_review_ifc_http_cannot_reuse_another_buildings_upload_on_either_alias()
 {
  var building=await SeedIfcBuilding();var second=await SeedIfcBuilding();var fake=new IfcMemoryStorage();
  using var host=factory!.WithWebHostBuilder(web=>web.ConfigureServices(services=>
  {
   services.PostConfigure<IfcUploadOptions>(o=>{o.Enabled=true;o.MaxBytes=1024;o.CleanupEnabled=true;});
   services.RemoveAll<IStorageService>();services.AddScoped<IStorageService>(_=>fake.Storage);
   services.RemoveAll<IIfcSourceInspector>();services.AddScoped<IIfcSourceInspector>(_=>fake.Inspector);
  }));
  using var http=host.CreateClient();http.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);http.DefaultRequestHeaders.Add("Idempotency-Key","http-building-binding");
  var input=new InitiateIfcUploadRequest(IfcBytes.Length,"model.ifc","v1",IfcHash);
  var created=await http.PostAsJsonAsync($"/api/buildings/{building}/ifc",input);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
  foreach(var path in new[]{$"/api/buildings/{second}/ifc",$"/api/buildings/{second}/revisions/upload-url"})
  {
   var conflict=await http.PostAsJsonAsync(path,input);Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);Assert.Contains("IDEMPOTENCY_KEY_CONFLICT",await conflict.Content.ReadAsStringAsync());
  }
  Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM ifc_upload_intents"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM revisions"));
 }

}
