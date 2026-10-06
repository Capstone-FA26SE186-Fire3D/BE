using System.Net;
using System.Text.Json;
using Amazon.S3;
using Fire3D.Application.Ifc;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.Infrastructure.Ifc;

public sealed class IfcUploadCleanupWorker(IServiceScopeFactory scopes, IOptions<IfcUploadOptions> options,
    ILogger<IfcUploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CleanupEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("IFC cleanup failed. ErrorType={ErrorType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var claim = new NpgsqlCommand("SELECT claim_ifc_object_cleanup()::text", connection);
        var raw = await claim.ExecuteScalarAsync(ct);
        if (raw is not string json) return false;
        using var document = JsonDocument.Parse(json);
        var key = document.RootElement.GetProperty("key").GetString()!;
        var token = document.RootElement.GetProperty("token").GetGuid();
        var attempt = document.RootElement.GetProperty("attempt").GetInt32();
        var success = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await scope.ServiceProvider.GetRequiredService<IStorageService>().DeleteObjectAsync(key, timeout.Token); success = true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { success = true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("IFC cleanup retry. Job={Job}, Attempt={Attempt}, ErrorType={ErrorType}", token, attempt, ex.GetType().Name); }
        await using var finish = new NpgsqlCommand("SELECT finish_ifc_object_cleanup(@key,@token,@success)", connection);
        finish.Parameters.AddWithValue("key",key); finish.Parameters.AddWithValue("token",token); finish.Parameters.AddWithValue("success",success);
        return (bool)(await finish.ExecuteScalarAsync(ct))!;
    }
}
