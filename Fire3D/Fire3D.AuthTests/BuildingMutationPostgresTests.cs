using System.Net;
using System.Net.Http.Json;
using Fire3D.API.Extensions;
using Fire3D.Application.Buildings;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Buildings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<Guid> SeedBuildingOrganization()
    {
        var organization = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO organizations(id,name,slug,is_active,metadata,created_at,updated_at) VALUES('{organization}','Building Test','{organization:N}',true,'{{}}',now(),now())");
        return organization;
    }

    [PostgresFact]
    public async Task Building_http_accepts_body_tenant_and_admin_updates_without_query_tenant()
    {
        var organization = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        using (var scope = factory!.Services.CreateScope())
        {
            var direct = await scope.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(new Fire3D.Application.Buildings.Commands.CreateBuilding.CreateBuildingCommand(adminId, null, new("Office", null, 1, null, null)));
            Assert.Equal(400, direct.Error?.Status);
        }
        var missing = await PostBuilding(client, new { name = "Office", totalFloors = 1 });
        Assert.True(missing.StatusCode == HttpStatusCode.BadRequest, await missing.Content.ReadAsStringAsync());
        Assert.Contains("organizationId", await missing.Content.ReadAsStringAsync());
        var mismatchRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/buildings?organizationId={Guid.NewGuid()}") { Content = JsonContent.Create(new { organizationId = organization, name = "Office", totalFloors = 1 }) };
        mismatchRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var mismatch = await client.SendAsync(mismatchRequest);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var created = await PostBuilding(client, new { organizationId = organization, name = "Office", totalFloors = 1 });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var building = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!;
        Assert.Equal(organization, building.OrganizationId);
        var updated = await SendBuilding(client, HttpMethod.Put, building.Id, new { name = "Updated", totalFloors = 2 }, created.Headers.ETag!.Tag);
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
        var tag = updated.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.OK, (await SendBuilding(client, HttpMethod.Delete, building.Id, null, tag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendBuilding(client, HttpMethod.Delete, building.Id, null, tag)).StatusCode);
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{building.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/buildings/{building.Id}")).StatusCode);
    }

    [PostgresFact]
    public async Task Building_insert_only_audit_runtime_rolls_back_all_mutation_when_audit_fails()
    {
        var organization = await SeedBuildingOrganization();
        await WithBuildingRuntime(async runtime =>
        {
            var id = Guid.NewGuid();
            await using (var db = BuildingContext(runtime))
                Assert.True(await new BuildingStore(db).CreateBuildingWithAuditAsync(NewBuilding(id, organization), null, null, adminId, DateTime.UtcNow, default));
            await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT reject_building_update CHECK(target_entity<>'buildings' OR action<>'Update') NOT VALID");
            await using (var db = BuildingContext(runtime))
            {
                var replacement = NewBuilding(id, organization); replacement.Name = "Must rollback";
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new BuildingStore(db).UpdateBuildingWithAuditAsync(replacement,
                    new BuildingLocation { Id = Guid.NewGuid(), BuildingId = id, Address = "Must rollback", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, null, adminId, DateTime.UtcNow, default));
                // The audit constraint must be the cause, not a concurrency failure of the new location insert.
                Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            }
            Assert.Equal("Office", await ScalarAsync($"SELECT name FROM buildings WHERE id='{id}'"));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM building_locations WHERE building_id='{id}'"));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{id}'"));
        });
    }

    [PostgresFact]
    public async Task Building_update_adds_a_first_location_and_contact()
    {
        var organization = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var created = await PostBuilding(client, new { organizationId = organization, name = "Bare", totalFloors = 1 });
        var id = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!.Id;
        var updated = await SendBuilding(client, HttpMethod.Put, id, new { name = "Bare", totalFloors = 1,
            location = new { address = "1 River St" }, contact = new { contactName = "Lan", isPrimary = true } }, created.Headers.ETag!.Tag);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_locations WHERE building_id='{id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_contacts WHERE building_id='{id}' AND contact_name='Lan'"));
        // A later update edits the same rows in place.
        var again = await SendBuilding(client, HttpMethod.Put, id, new { name = "Bare", totalFloors = 1,
            location = new { address = "2 River St" }, contact = new { contactName = "Mai", isPrimary = true } }, updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_contacts WHERE building_id='{id}' AND contact_name='Mai'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_locations WHERE building_id='{id}' AND address='2 River St'"));
    }

    [PostgresFact]
    public Task Building_waiting_on_lifecycle_lock_rechecks_disabled_organization_before_writing() => BuildingLifecycleRace(true);

    [PostgresFact]
    public Task Building_waiting_on_lifecycle_lock_rechecks_disabled_account_before_writing() => BuildingLifecycleRace(false);

    private async Task BuildingLifecycleRace(bool disableOrganization)
    {
        var organization = await SeedBuildingOrganization();
        await WithBuildingRuntime(async runtime =>
        {
            await using var writer = new NpgsqlConnection(testConnection);
            await writer.OpenAsync(); await using var transaction = await writer.BeginTransactionAsync();
            await new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('fire3d:identity-management',0))", writer, transaction).ExecuteNonQueryAsync();
            await using var db = BuildingContext(runtime);
            var id = Guid.NewGuid();
            var pending = new BuildingStore(db).CreateBuildingWithAuditAsync(NewBuilding(id, organization), null, null, adminId, DateTime.UtcNow, default);
            var waiting = false;
            for (var i = 0; i < 100 && !waiting; i++)
            {
                waiting = (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"))!;
                if (!waiting) await Task.Delay(25);
            }
            Assert.True(waiting, "Mutation must acquire the lifecycle lock before authorization.");
            await new NpgsqlCommand(disableOrganization
                ? $"UPDATE organizations SET is_active=false WHERE id='{organization}'"
                : $"UPDATE users SET is_active=false WHERE id='{adminId}'", writer, transaction).ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM buildings WHERE id='{id}'"));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{id}'"));
        });
    }

    private Building NewBuilding(Guid id, Guid organization) => new()
    {
        Id = id, OrganizationId = organization, Name = "Office", TotalFloors = 1, IsActive = true,
        CreatedBy = adminId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    private static readonly Npgsql.NameTranslation.NpgsqlNullNameTranslator BuildingEnumNames = new();
    private static Fire3DDbContext BuildingContext(string connection)
    {
        var options = new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(connection, pg =>
        {
            var names = BuildingEnumNames;
            pg.MapEnum<Fire3D.Domain.Enums.UserRole>("user_role_enum", nameTranslator: names);
            pg.MapEnum<Fire3D.Domain.Enums.AuditAction>("audit_action_enum", nameTranslator: names);
        }).Options;
        return new Fire3DDbContext(options);
    }
    private async Task WithBuildingRuntime(Func<string,Task> run)
    {
        var login = "building_test_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}'; GRANT USAGE ON SCHEMA public TO {login}; GRANT SELECT ON users,organizations,buildings,building_locations,building_contacts TO {login}; GRANT INSERT,UPDATE ON buildings,building_locations,building_contacts TO {login}; GRANT INSERT ON audit_logs TO {login};");
        foreach (var table in new[] { "users", "organizations", "buildings", "building_locations", "building_contacts", "audit_logs" })
            await ExecuteAsync($"CREATE POLICY {login} ON {table} TO {login} USING(true) WITH CHECK(true)");
        try
        {
            Assert.Equal(false, await ScalarAsync($"SELECT has_table_privilege('{login}','audit_logs','SELECT')"));
            await run(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
        }
        finally
        {
            foreach (var table in new[] { "users", "organizations", "buildings", "building_locations", "building_contacts", "audit_logs" })
                await ExecuteAsync($"DROP POLICY {login} ON {table}");
            await ExecuteAsync($"DROP OWNED BY {login}; DROP ROLE {login}");
        }
    }
}



