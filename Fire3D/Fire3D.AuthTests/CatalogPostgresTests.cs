using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<JsonObject> AdminJson(HttpMethod method,string url,object? body=null,string? key=null,string? ifMatch=null)
    {
        var request=new HttpRequestMessage(method,url){Content=body is null?null:JsonContent.Create(body)};
        if(key is not null)request.Headers.Add("Idempotency-Key",key);
        if(ifMatch is not null)request.Headers.TryAddWithoutValidation("If-Match",ifMatch);
        var response=await client.SendAsync(request);var text=await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,text);return JsonNode.Parse(text)!.AsObject();
    }

    [PostgresFact]
    public async Task Catalogs_list_common_sources_situations_and_rubric_contract_for_the_editor()
    {
        var tenant=await SeedAiTenant();
        client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
        string Hash(string s)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
        var draft=await AdminJson(HttpMethod.Post,"/api/admin/knowledge-sources",new {visibility="Common",title="QCVN 06:2022 fire safety",versionLabel="2022",sourceHash=Hash("a"),jurisdiction="VN"},"src-draft");
        var approved=await AdminJson(HttpMethod.Post,"/api/admin/knowledge-sources",new {visibility="Common",title="TCVN 3890 extinguishers 100%",versionLabel="2023",sourceHash=Hash("b")},"src-approved");
        await AdminJson(HttpMethod.Post,$"/api/admin/knowledge-sources/{approved["id"]}/approve",null,null,"\"1\"");
        await AdminJson(HttpMethod.Post,"/api/admin/knowledge-sources",new {visibility="Organization",organizationId=tenant.Org,title="Tenant manual",versionLabel="1",sourceHash=Hash("c")},"src-tenant");

        // Common is the default scope; tenant sources never appear unless asked for explicitly.
        var common=await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources");
        Assert.Equal(2,common["total"]!.GetValue<int>());
        Assert.DoesNotContain(common["items"]!.AsArray(),x=>x!["visibility"]!.GetValue<string>()!="Common");
        Assert.Equal(1,(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?visibility=Organization"))["total"]!.GetValue<int>());
        var approvedRow=(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?status=Approved"))["items"]![0]!;
        Assert.True(approvedRow["canUseInDraft"]!.GetValue<bool>());Assert.True(approvedRow["canPublishReference"]!.GetValue<bool>());
        var draftRow=(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?search=QCVN"))["items"]!.AsArray().Single()!;
        Assert.True(draftRow["canUseInDraft"]!.GetValue<bool>());Assert.False(draftRow["canPublishReference"]!.GetValue<bool>());
        // Search treats wildcard characters literally.
        Assert.Equal(1,(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?search=100%25"))["total"]!.GetValue<int>());
        Assert.Equal(0,(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?search=_%25_"))["total"]!.GetValue<int>());
        Assert.Equal(1,(await AdminJson(HttpMethod.Get,"/api/admin/knowledge-sources?pageSize=1&page=2"))["items"]!.AsArray().Count);
        // A chosen source can be reopened by ID after a reload, with the same flags.
        var detail=await AdminJson(HttpMethod.Get,$"/api/admin/knowledge-sources/{draft["id"]}");
        Assert.True(detail["canUseInDraft"]!.GetValue<bool>());Assert.False(detail["canPublishReference"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/admin/knowledge-sources?visibility=Tenant")).StatusCode);

        // Situations: paged admin catalog with search and isActive; the public list is unchanged.
        await AdminJson(HttpMethod.Post,"/api/admin/learn/situations",new {slug="fire-at-home",name="Cháy tại nhà"},"sit-1");
        await AdminJson(HttpMethod.Post,"/api/admin/learn/situations",new {slug="fire-at-office",name="Cháy văn phòng"},"sit-2");
        await ExecuteAsync("UPDATE learn_situations SET is_active=false WHERE slug='fire-at-office'");
        Assert.Equal(2,(await AdminJson(HttpMethod.Get,"/api/admin/learn/situations"))["total"]!.GetValue<int>());
        Assert.Equal("fire-at-home",(await AdminJson(HttpMethod.Get,"/api/admin/learn/situations?isActive=true"))["items"]![0]!["slug"]!.GetValue<string>());
        Assert.Equal(1,(await AdminJson(HttpMethod.Get,"/api/admin/learn/situations?search=office"))["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/admin/learn/situations?pageSize=101")).StatusCode);
        using var anonymous=factory!.CreateClient();
        Assert.Single(JsonNode.Parse(await anonymous.GetStringAsync("/api/learn/situations"))!.AsArray());

        // Rubric catalog for PlatformAdmin and OrganizationUser; others are refused.
        var catalog=await AdminJson(HttpMethod.Get,"/api/scenario-rubrics/catalog");
        Assert.Equal("1",catalog["defaultSchemaVersion"]!.GetValue<string>());
        Assert.Equal(["1"],catalog["supportedSchemaVersions"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/scenario-rubrics/catalog")).StatusCode);
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        using var org=factory.CreateClient();org.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync($"ai-{tenant.User:N}@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.OK,(await org.GetAsync("/api/scenario-rubrics/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await org.GetAsync("/api/admin/knowledge-sources")).StatusCode);
    }
}
