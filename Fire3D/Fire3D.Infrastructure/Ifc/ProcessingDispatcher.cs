using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Ifc;
public sealed class ProcessingDispatcher(IServiceScopeFactory scopes,IHttpClientFactory clients,IOptions<ProcessingWorkerOptions> options,ILogger<ProcessingDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Value.DispatcherEnabled)return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        do { try { await RunOnceAsync(stoppingToken); } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex) { log.LogWarning("Processing dispatcher failed: {ErrorType}",ex.GetType().Name); }
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope=scopes.CreateScope();var gates=scope.ServiceProvider.GetRequiredService<IProcessingDispatchGate>();
        await gates.ExecuteAsync("Recover",null,null,null,ct);
        var delivery=await gates.ExecuteAsync("Claim",null,null,null,ct);if(delivery.GetProperty("code").GetString()=="EMPTY")return false;
        var key=delivery.GetProperty("eventKey").GetString()!;var lease=delivery.GetProperty("leaseToken").GetGuid();
        var acknowledged=false;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request=new HttpRequestMessage(HttpMethod.Post,options.Value.WorkerUrl) { Content=JsonContent.Create(delivery) };
            request.Headers.Add("X-Worker-Key",options.Value.MachineKey);
            using var response=await clients.CreateClient("processing-worker").SendAsync(request,timeout.Token);
            if(response.IsSuccessStatusCode)
            {
                var reply=await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                if(reply.TryGetProperty("eventKey",out var eventKey) && eventKey.GetString()==key && reply.TryGetProperty("payloadHash",out var hash) && hash.GetString()==delivery.GetProperty("payloadHash").GetString() && reply.TryGetProperty("receiptId",out var id) && id.TryGetGuid(out var receipt))
                    acknowledged=(await gates.ExecuteAsync("Ack",key,lease,receipt,ct)).GetProperty("code").GetString()=="OK";
            }
            log.LogInformation("Processing delivery result: Attempt={Attempt}, ProviderStatus={ProviderStatus}, Acknowledged={Acknowledged}",delivery.GetProperty("attempt").GetInt32(),(int)response.StatusCode,acknowledged);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception ex) { log.LogWarning("Processing delivery failed: Attempt={Attempt}, ErrorType={ErrorType}",delivery.GetProperty("attempt").GetInt32(),ex.GetType().Name); }
        if(!acknowledged)await gates.ExecuteAsync("Fail",key,lease,null,ct);
        return acknowledged;
    }
}
