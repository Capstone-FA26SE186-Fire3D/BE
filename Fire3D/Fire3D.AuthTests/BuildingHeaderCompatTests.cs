using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Buildings;
using Fire3D.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // FE#48 transition: with Compatibility:BuildingHeadersOptional=true, writes without the BE#94 headers succeed with a Warning;
    // malformed headers are still refused. The default (false) is covered by Building_create_is_idempotent_and_updates_need_a_current_etag.
    // The flag is read at startup into a singleton; the test turns it on for the running host and off again (tests run serially).
    [PostgresFact]
    public async Task Building_writes_without_headers_pass_only_while_the_compatibility_flag_is_on()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var owner = await CreateOrganizationAsync("bld-compat-" + Guid.NewGuid().ToString("N")[..8]);
        await CreateAccountAsync("compat-owner@example.test", UserRole.OrganizationUser, owner.Id);
        using var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("compat-owner@example.test")).AccessToken);
        var compatibility = factory.Services.GetRequiredService<Fire3D.API.Controllers.BuildingHeaderCompatibility>();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBuilding(http, new { name = "Strict", totalFloors = 2 }, key: null)).StatusCode);
        compatibility.HeadersOptional = true;
        try
        {
            var created = await PostBuilding(http, new { name = "Legacy tower", totalFloors = 2 }, key: null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Contains("Idempotency-Key", string.Join(",", created.Headers.GetValues("Warning")));
            var building = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!;
            Assert.Equal(HttpStatusCode.BadRequest, (await PostBuilding(http, new { name = "Bad key", totalFloors = 2 }, key: "has space")).StatusCode);

            var updated = await SendBuilding(http, HttpMethod.Put, building.Id, new { name = "Legacy tower A", totalFloors = 3 }, null);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Contains("If-Match", string.Join(",", updated.Headers.GetValues("Warning")));
            Assert.Equal(HttpStatusCode.BadRequest, (await SendBuilding(http, HttpMethod.Put, building.Id, new { name = "x", totalFloors = 3 }, "W/abc")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendBuilding(http, HttpMethod.Delete, building.Id, null, null)).StatusCode);
            Assert.False((bool)(await ScalarAsync($"SELECT is_active FROM buildings WHERE id='{building.Id}'"))!);
        }
        finally { compatibility.HeadersOptional = false; }
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBuilding(http, new { name = "Strict again", totalFloors = 2 }, key: null)).StatusCode);
    }
}
