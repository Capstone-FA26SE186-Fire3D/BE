using Amazon.S3;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarCleanupWorkerTests
{
    [Theory]
    [InlineData(0)] // Protected object: retry, never delete.
    [InlineData(1)] // Expired lease: never delete.
    [InlineData(2)] // S3 NotFound: complete.
    [InlineData(3)] // Temporary failure: retry without exposing key/provider text.
    public async Task Worker_obeys_protection_fencing_and_provider_error_semantics(int mode)
    {
        var job=new AvatarCleanupJob(Guid.NewGuid(),"avatars/private-sensitive-key",Guid.NewGuid(),3);
        var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed=0;var deletes=0;var completed=0;var retried=0;
        var cleanup=ResetProxy.For<IAvatarCleanupStore>((method,_)=>method switch
        {
            "ClaimAsync"=>Task.FromResult<AvatarCleanupJob?>(Interlocked.Increment(ref claimed)==1?job:null),
            "IsReferencedAsync"=>Task.FromResult(mode==0),
            "RenewAsync"=>Renew(),
            "CompleteAsync"=>Complete(),
            "FailAsync"=>Retry(),
            _=>throw new InvalidOperationException(method)
        });
        Task<bool> Renew(){if(mode==1)signal.TrySetResult();return Task.FromResult(mode!=1);}
        Task Complete(){completed++;signal.TrySetResult();return Task.CompletedTask;}
        Task Retry(){retried++;signal.TrySetResult();return Task.CompletedTask;}
        var storage=ResetProxy.For<IStorageService>((method,_)=>
        {
            Assert.Equal("DeleteObjectAsync",method);deletes++;
            return Task.FromException(new AmazonS3Exception("secret "+job.ObjectKey) { StatusCode=mode==2?System.Net.HttpStatusCode.NotFound:System.Net.HttpStatusCode.ServiceUnavailable });
        });
        var services=new ServiceCollection().AddSingleton(cleanup).AddSingleton(storage);
        using var provider=services.BuildServiceProvider();var logger=new Capture();
        using var worker=new AvatarCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(),logger);
        await worker.StartAsync(default);
        try{await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));}finally{await worker.StopAsync(default);}
        Assert.Equal(mode>=2?1:0,deletes);Assert.Equal(mode==2?1:0,completed);Assert.Equal(mode is 0 or 3?1:0,retried);
        Assert.All(logger.Messages,message=>Assert.DoesNotContain(job.ObjectKey,message));
        Assert.All(logger.Exceptions,exception=>Assert.Null(exception));
    }
    [Theory]
    [InlineData("deleted")]  // Provider delete succeeded: the job completes.
    [InlineData("shutdown")] // Stopped mid-delete: neither completed nor retried; the lease expires and the job is reclaimed.
    public async Task Worker_completes_after_a_delete_and_leaves_the_job_owned_when_stopped_mid_delete(string mode)
    {
        var job=new AvatarCleanupJob(Guid.NewGuid(),"avatars/private-sensitive-key",Guid.NewGuid(),1);
        var step=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed=0;var outcome=new List<string>();
        var cleanup=ResetProxy.For<IAvatarCleanupStore>((method,_)=>method switch
        {
            "ClaimAsync"=>Task.FromResult<AvatarCleanupJob?>(Interlocked.Increment(ref claimed)==1?job:null),
            "IsReferencedAsync"=>Task.FromResult(false),
            "RenewAsync"=>Task.FromResult(true),
            "CompleteAsync" or "FailAsync"=>Record(method),
            _=>throw new InvalidOperationException(method)
        });
        Task Record(string method){outcome.Add(method);step.TrySetResult();return Task.CompletedTask;}
        var deleted=new List<string>();
        var storage=ResetProxy.For<IStorageService>((method,args)=>
        {
            Assert.Equal("DeleteObjectAsync",method);deleted.Add((string)args[0]!);
            if(mode=="deleted")return Task.CompletedTask;
            step.TrySetResult();return Task.Delay(Timeout.Infinite,(CancellationToken)args[1]!);
        });
        using var provider=new ServiceCollection().AddSingleton(cleanup).AddSingleton(storage).BuildServiceProvider();var logger=new Capture();
        using var worker=new AvatarCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(),logger);
        await worker.StartAsync(default);
        try{await step.Task.WaitAsync(TimeSpan.FromSeconds(5));}finally{await worker.StopAsync(default);}
        Assert.Equal(new[]{job.ObjectKey},deleted);
        Assert.Equal(mode=="deleted"?new[]{"CompleteAsync"}:Array.Empty<string>(),outcome);
        Assert.Empty(logger.Messages);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(true)]  // Missing grant: an operator problem, logged by SQLSTATE and retried after a minute.
    [InlineData(false)] // Any other claim failure: logged by type only and claimed again after ten seconds.
    public async Task Claim_failures_are_logged_without_provider_details_and_the_worker_backs_off(bool missingGrant)
    {
        var step=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var claims=0;
        var cleanup=ResetProxy.For<IAvatarCleanupStore>((method,_)=>method!="ClaimAsync"?throw new InvalidOperationException(method)
            :Interlocked.Increment(ref claims)==1
                ?Task.FromException<AvatarCleanupJob?>(missingGrant
                    ?new Npgsql.PostgresException("permission denied for table avatar_cleanup_jobs","ERROR","ERROR",Npgsql.PostgresErrorCodes.InsufficientPrivilege)
                    :new InvalidOperationException("secret connection detail"))
                :Retried());
        Task<AvatarCleanupJob?> Retried(){step.TrySetResult();return Task.FromResult<AvatarCleanupJob?>(null);}
        using var provider=new ServiceCollection().AddSingleton(cleanup).BuildServiceProvider();
        var logger=new Capture{Logged=()=>{if(missingGrant)step.TrySetResult();}};
        using var worker=new AvatarCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(),logger);
        await worker.StartAsync(default);
        try{await step.Task.WaitAsync(TimeSpan.FromSeconds(20));}finally{await worker.StopAsync(default);}
        Assert.Equal(missingGrant?"Avatar cleanup blocked. SQLSTATE=42501; retry in 60 seconds.":"Avatar cleanup worker failed (InvalidOperationException).",Assert.Single(logger.Messages));
        Assert.Null(Assert.Single(logger.Exceptions));
        // A missing grant waits a full minute (shutdown cancels that wait); another failure is claimed again after the short back-off.
        Assert.Equal(missingGrant?1:2,claims);
        Assert.True(worker.ExecuteTask!.IsCompleted);Assert.False(worker.ExecuteTask.IsFaulted);
    }
    private sealed class Capture : ILogger<AvatarCleanupWorker>
    {
        public List<string> Messages {get;}=[];public List<Exception?> Exceptions {get;}=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter){Messages.Add(formatter(state,exception));Exceptions.Add(exception);Logged?.Invoke();}
        public Action? Logged;
    }
}
