using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Application.Buildings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Readiness_reports_pending_migrations_and_liveness_stays_process_only()
    {
        var ready=await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK,ready.StatusCode);
        var body=JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Healthy",body.GetProperty("status").GetString());
        Assert.Equal("OK",body.GetProperty("checks").GetProperty("database").GetProperty("code").GetString());
        Assert.Equal("Healthy",await client.GetStringAsync("/health"));

        // The restricted runtime login (member of fire3d_api, like Azure) can verify the migration history.
        var login="ready_runtime_"+Guid.NewGuid().ToString("N");var password=new NpgsqlConnectionStringBuilder(testConnection).Password??"";
        await using(var admin=new NpgsqlConnection(adminConnection)){await admin.OpenAsync();await new NpgsqlCommand($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{password.Replace("'","''")}' IN ROLE fire3d_api",admin).ExecuteNonQueryAsync();}
        try
        {
            var runtime=new ServiceCollection();
            runtime.AddDbContext<Fire3DDbContext>(options=>options.UseNpgsql(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Pooling=false}.ConnectionString));
            await using var runtimeProvider=runtime.BuildServiceProvider();
            var restricted=await new DatabaseReadinessCheck(runtimeProvider.GetRequiredService<IServiceScopeFactory>()).CheckHealthAsync(new HealthCheckContext(),default);
            Assert.Equal(HealthStatus.Healthy,restricted.Status);Assert.Equal("OK",restricted.Description);
            await using(var admin=new NpgsqlConnection(testConnection)){await admin.OpenAsync();await new NpgsqlCommand("REVOKE SELECT ON \"__EFMigrationsHistory\" FROM fire3d_api",admin).ExecuteNonQueryAsync();}
            var unreadable=await new DatabaseReadinessCheck(runtimeProvider.GetRequiredService<IServiceScopeFactory>()).CheckHealthAsync(new HealthCheckContext(),default);
            Assert.Equal("MIGRATION_HISTORY_UNREADABLE",unreadable.Description);
        }
        finally
        {
            await using var admin=new NpgsqlConnection(testConnection);await admin.OpenAsync();
            await new NpgsqlCommand($"GRANT SELECT ON \"__EFMigrationsHistory\" TO fire3d_api;DROP OWNED BY {login};DROP ROLE {login}",admin).ExecuteNonQueryAsync();
        }

        // A database that lags the build is reported with the missing migration IDs, never a connection string.
        var empty="fire3d_ready_"+Guid.NewGuid().ToString("N");
        await using(var admin=new NpgsqlConnection(adminConnection)){await admin.OpenAsync();await new NpgsqlCommand($"CREATE DATABASE \"{empty}\"",admin).ExecuteNonQueryAsync();}
        try
        {
            var services=new ServiceCollection();
            services.AddDbContext<Fire3DDbContext>(options=>options.UseNpgsql(new NpgsqlConnectionStringBuilder(adminConnection){Database=empty}.ConnectionString));
            await using var provider=services.BuildServiceProvider();
            var result=await new DatabaseReadinessCheck(provider.GetRequiredService<IServiceScopeFactory>()).CheckHealthAsync(new HealthCheckContext(),default);
            Assert.Equal(HealthStatus.Unhealthy,result.Status);Assert.Equal("MIGRATIONS_PENDING",result.Description);
            Assert.NotEmpty((string[])result.Data["pendingMigrations"]);
            Assert.DoesNotContain("Host=",JsonSerializer.Serialize(result.Data),StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain(empty,JsonSerializer.Serialize(result.Data));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin=new NpgsqlConnection(adminConnection);await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{empty}\" WITH (FORCE)",admin).ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Unhandled_server_errors_carry_a_stable_code_and_trace_id()
    {
        using var scope=factory!.Services.CreateScope();
        var problems=scope.ServiceProvider.GetRequiredService<IProblemDetailsService>();
        var context=new DefaultHttpContext{RequestServices=scope.ServiceProvider};context.Response.Body=new MemoryStream();
        context.Response.StatusCode=500;
        Assert.True(await problems.TryWriteAsync(new ProblemDetailsContext{HttpContext=context,ProblemDetails={Status=500,Title="An error occurred while processing your request."}}));
        context.Response.Body.Position=0;
        var problem=JsonDocument.Parse(context.Response.Body).RootElement;
        Assert.Equal("INTERNAL_ERROR",problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [PostgresFact]
    public async Task Organization_user_creates_reads_lists_and_archives_a_building_over_HTTP()
    {
        var tenant=await SeedAiTenant();
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        using var org=factory!.CreateClient();
        org.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync($"ai-{tenant.User:N}@example.test")).AccessToken);
        var created=await PostBuilding(org,new{name="HQ",buildingType="Office",totalFloors=3,location=new{address="1 Example Street",city="Hanoi"}});
        Assert.True(created.StatusCode==HttpStatusCode.Created,await created.Content.ReadAsStringAsync());
        var building=(await created.Content.ReadFromJsonAsync<BuildingResponse>())!;
        Assert.Equal(tenant.Org,building.OrganizationId);
        // The detail and the child lists that returned 500 on the broken deployment.
        var detail=await org.GetAsync($"/api/buildings/{building.Id}");
        Assert.True(detail.StatusCode==HttpStatusCode.OK,await detail.Content.ReadAsStringAsync());
        Assert.Contains("1 Example Street",await detail.Content.ReadAsStringAsync());
        foreach(var path in new[]{$"/api/buildings/{building.Id}/revisions?page=1&pageSize=5",$"/api/buildings/{building.Id}/scenarios",$"/api/buildings/{building.Id}/access",$"/api/buildings/{building.Id}/trainings"})
        {
            var response=await org.GetAsync(path);
            Assert.True(response.StatusCode==HttpStatusCode.OK,path+" "+await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK,(await SendBuilding(org,HttpMethod.Delete,building.Id,null,detail.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await org.GetAsync($"/api/buildings/{building.Id}")).StatusCode);
    }
}
