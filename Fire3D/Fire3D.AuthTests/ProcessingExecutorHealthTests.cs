using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#118: production answered 500 because the ProcessingExecutor login was not a member of fet3d_processing_executor.
    // That configuration now answers 503 PROCESSING_EXECUTOR_FORBIDDEN and GET /health/worker reports it; /health/ready is unaffected.
    [PostgresFact]
    public async Task Worker_requests_report_an_executor_login_without_the_role_as_503_and_health_worker_shows_it()
    {
        var plain = "plain_login_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {plain} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {plain}");
        try
        {
            async Task Check(string executor, HttpStatusCode lease, HttpStatusCode worker, string code)
            {
                var key = new string('k', 48);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = executor }).Build();
                using var host = factory!.WithWebHostBuilder(web => web
                    .ConfigureAppConfiguration((_, b) => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = executor }))
                    .ConfigureServices(services =>
                    {
                        services.PostConfigure<ProcessingWorkerOptions>(o => { o.WorkerApiEnabled = true; o.MachineKey = key; o.AllowedToolchains = ["fake-unity-v1"]; o.PackagePullEnabled = true; });
                        services.RemoveAll<IProcessingWorkerGate>();
                        services.AddScoped<IProcessingWorkerGate>(p => new ProcessingRuntimeStore(p.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(), config));
                    }));
                using var http = host.CreateClient();
                // The worker health codes describe the deployment: machine key only.
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/health/worker")).StatusCode);
                http.DefaultRequestHeaders.Add("X-Worker-Key", key);
                var response = await http.PostAsJsonAsync("/internal/processing/package-jobs/lease", new { toolchainVersion = "fake-unity-v1" });
                Assert.Equal(lease, response.StatusCode);
                if (lease == HttpStatusCode.ServiceUnavailable) Assert.Contains(code, await response.Content.ReadAsStringAsync());
                var health = await http.GetAsync("/health/worker");
                Assert.Equal(worker, health.StatusCode);
                Assert.Contains(code, await health.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            }
            await Check(new NpgsqlConnectionStringBuilder(testConnection) { Username = plain, Pooling = false }.ConnectionString,
                HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, "PROCESSING_EXECUTOR_FORBIDDEN");
            await Check(new NpgsqlConnectionStringBuilder(testConnection) { Username = plain, Password = "wrong-password", Pooling = false }.ConnectionString,
                HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, "PROCESSING_EXECUTOR_LOGIN_FAILED");
            await WithProcessingRuntime((_, worker) => Check(worker, HttpStatusCode.NoContent, HttpStatusCode.OK, "\"OK\""));
        }
        finally { await ExecuteAsync($"DROP OWNED BY {plain};DROP ROLE {plain}"); }
    }
}

public sealed class ProcessingExecutorCodeTests
{
    // BE#118 (18/10/2026): production lease answered 500 on the Supavisor circuit breaker of the executor login.
    [Theory]
    [InlineData("XX000", "(ECIRCUITBREAKER) failed to retrieve database credentials after multiple attempts, new connections are temporarily blocked", "PROCESSING_EXECUTOR_LOGIN_FAILED")]
    [InlineData("XX000", "Tenant or user not found", "PROCESSING_EXECUTOR_LOGIN_FAILED")]
    [InlineData("28P01", "password authentication failed for user \"fet3d_processing_worker\"", "PROCESSING_EXECUTOR_LOGIN_FAILED")]
    [InlineData("42501", "permission denied for function processing_package_lease_gate", "PROCESSING_EXECUTOR_FORBIDDEN")]
    [InlineData("42501", "permission denied for table processing_jobs", null)]
    [InlineData("XX000", "unexpected internal error", null)]
    public void Executor_login_errors_map_to_configuration_codes(string sqlState, string message, string? code)
    {
        var error = new PostgresException(message, "FATAL", "FATAL", sqlState);
        Assert.Equal(code, ProcessingExecutorUnavailableException.CodeFor(error));
        Assert.Equal(code, ProcessingExecutorUnavailableException.CodeFor(new InvalidOperationException("wrapped", error)));
    }
}
