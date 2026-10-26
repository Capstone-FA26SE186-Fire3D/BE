using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Counts every command EF sends; the API database is about 60 ms away in production, so each one is a round trip.</summary>
public sealed class DatabaseCommandCounter : DbCommandInterceptor
{
    private int count;
    public int Count => Volatile.Read(ref count);
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
    { Interlocked.Increment(ref count); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result)
    { Interlocked.Increment(ref count); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData data, InterceptionResult<object> result)
    { Interlocked.Increment(ref count); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken ct = default)
    { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
}

public sealed partial class AuthIntegrationTests
{
    // BE#168: an OrganizationUser read costs the bearer session check plus its own queries. Authorization reuses the
    // account the session check just loaded, and list endpoints fold existence, total and page into one query.
    [PostgresFact]
    public async Task Organization_reads_stay_within_their_database_round_trip_budget()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var owner = await CreateOrganizationAsync("trips-" + Guid.NewGuid().ToString("N")[..8]);
        var stranger = await CreateOrganizationAsync("trips-x-" + Guid.NewGuid().ToString("N")[..8]);
        await CreateAccountAsync("trips-owner@example.test", UserRole.OrganizationUser, owner.Id);
        await CreateAccountAsync("trips-other@example.test", UserRole.OrganizationUser, stranger.Id);
        using var org = factory!.CreateClient();
        org.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("trips-owner@example.test")).AccessToken);
        using var other = factory!.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("trips-other@example.test")).AccessToken);

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/buildings") { Content = JsonContent.Create(new { name = "Trips", totalFloors = 2 }) };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var created = await org.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var building = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var scenarioResponse = await org.SendAsync(Keyed(HttpMethod.Post, "/api/scenarios", new { buildingId = building, name = "Trips" }, "trips-scenario-" + building));
        Assert.Equal(HttpStatusCode.Created, scenarioResponse.StatusCode);
        var scenario = (await scenarioResponse.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();

        async Task<(int Trips, JsonObject Body)> Read(HttpClient http, string url, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var before = databaseCommands.Count;
            var response = await http.GetAsync(url);
            var trips = databaseCommands.Count - before;
            Assert.True(response.StatusCode == expected, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return (trips, expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<JsonObject>())! : new JsonObject());
        }

        // 1 = session check, 1 = the read itself.
        Assert.Equal(2, (await Read(org, $"/api/buildings/{building}")).Trips);
        Assert.Equal(2, (await Read(org, $"/api/scenarios/{scenario}")).Trips);

        var buildings = await Read(org, "/api/buildings?page=1&pageSize=20");
        Assert.Equal(2, buildings.Trips);
        Assert.Equal(1, buildings.Body["totalCount"]!.GetValue<int>());
        // Past the last page no row carries the total, so only that case counts separately.
        var pastEnd = await Read(org, "/api/buildings?page=5&pageSize=20");
        Assert.Equal(3, pastEnd.Trips);
        Assert.Empty(pastEnd.Body["items"]!.AsArray());
        Assert.Equal(1, pastEnd.Body["totalCount"]!.GetValue<int>());

        var scenarios = await Read(org, $"/api/buildings/{building}/scenarios?page=1&pageSize=20");
        Assert.Equal(2, scenarios.Trips);
        Assert.Equal(scenario, Assert.Single(scenarios.Body["items"]!.AsArray())!["id"]!.GetValue<Guid>());
        var scenariosPastEnd = await Read(org, $"/api/buildings/{building}/scenarios?page=3&pageSize=20");
        Assert.Empty(scenariosPastEnd.Body["items"]!.AsArray());
        Assert.Equal(1, scenariosPastEnd.Body["totalCount"]!.GetValue<int>());

        // Versions: session and the folded list; the review-state gate adds one more only when the page has versions.
        var versions = await Read(org, $"/api/scenarios/{scenario}/versions?page=1&pageSize=20");
        Assert.Equal(2, versions.Trips);
        Assert.Equal(0, versions.Body["totalCount"]!.GetValue<int>());

        var releases = await Read(org, $"/api/releases?scenarioId={scenario}");
        Assert.Equal(2, releases.Trips);
        Assert.Equal(0, releases.Body["totalCount"]!.GetValue<int>());

        // Folding the existence check into the list keeps tenant isolation: another organization still gets 404.
        await Read(other, $"/api/scenarios/{scenario}/versions", HttpStatusCode.NotFound);
        await Read(other, $"/api/buildings/{building}/scenarios", HttpStatusCode.NotFound);
        Assert.Equal(0, (await Read(other, "/api/buildings?page=1&pageSize=20")).Body["totalCount"]!.GetValue<int>());
    }
}
