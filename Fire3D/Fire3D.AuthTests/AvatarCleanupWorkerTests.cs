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
    private sealed class Capture : ILogger<AvatarCleanupWorker>
    {
        public List<string> Messages {get;}=[];public List<Exception?> Exceptions {get;}=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter){Messages.Add(formatter(state,exception));Exceptions.Add(exception);}
    }
}
