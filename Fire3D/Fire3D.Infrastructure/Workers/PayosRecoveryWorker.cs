using Fire3D.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Workers;
public sealed class PayosRecoveryWorker(IServiceScopeFactory scopes,IOptions<PayosOptions> options,ILogger<PayosRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.WorkerEnabled){logger.LogInformation("PayOS recovery worker is disabled.");return;}
        logger.LogInformation("PayOS recovery worker enabled; checkout enabled {Enabled}, interval {Seconds}s",options.Value.Enabled,options.Value.PollSeconds);
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        do
        {
            try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<IPayosPayments>().Recover(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("PayOS recovery batch failed ({ExceptionType}); will retry",ex.GetType().Name);}
            try{if(!await timer.WaitForNextTickAsync(ct))break;}catch(OperationCanceledException){break;}
        }while(!ct.IsCancellationRequested);
    }
}
