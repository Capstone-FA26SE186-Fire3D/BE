using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Email;
using Fire3D.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Runs a hosted worker until one observable step happened, then stops it through the stopping token.</summary>
internal static class WorkerHarness
{
    public static async Task Run(BackgroundService worker,Task step)
    {
        await worker.StartAsync(default);
        try{await step.WaitAsync(TimeSpan.FromSeconds(15));}
        finally{await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(15));}
    }
    public static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IServiceScopeFactory Scopes(Action<ServiceCollection> register)
    {
        var services=new ServiceCollection();register(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
    // Blocks like a slow provider until the worker is stopped.
    public static async Task UntilStopped(CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);}
}

internal sealed class CapturedLog<T> : ILogger<T>
{
    public readonly List<(LogLevel Level,string Message,Exception? Exception)> Entries=[];
    public Action<LogLevel,string>? Logged;
    public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
    public bool IsEnabled(LogLevel level)=>true;
    public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
    {
        var message=formatter(state,exception);
        lock(Entries)Entries.Add((level,message,exception));
        Logged?.Invoke(level,message);
    }
    public string All(){lock(Entries)return string.Join("\n",Entries.Select(x=>x.Message));}
}

public sealed class EmailWorkerLoopTests
{
    private const string Recipient="private-recipient@example.test";
    private static IOptions<AuthEmailOptions> Enabled=>Options.Create(new AuthEmailOptions{WorkerEnabled=true});
    private sealed class Mail : IEmailService
    {
        public readonly List<(string To,string Subject,string Html)> Sent=[];
        public Func<CancellationToken,Task>? Send;
        public async Task SendAsync(string to,string subject,string html,CancellationToken ct)
        {if(Send is not null)await Send(ct);Sent.Add((to,subject,html));}
    }
    // Outcome of one claimed job: "complete" or "fail:<permanent>"; null while the job is still owned (shutdown mid-send).
    private sealed class Outcome
    {
        public readonly TaskCompletionSource Done=WorkerHarness.Signal();public string? Result;public int Claims;
        public Task Finish(string result){Result=result;Done.TrySetResult();return Task.CompletedTask;}
        // Hands out the job once; with a polling error, the first claim fails and the retry gets the job.
        public Task<T?> Claim<T>(T? job,Func<Exception>? error) where T:class
        {
            var claim=Interlocked.Increment(ref Claims);
            if(error is not null){if(claim==1)return Task.FromException<T?>(error());claim--;}
            return Task.FromResult(claim==1?job:null);
        }
    }
    private static Exception Failure(string kind)=>kind switch
    {
        "permanent"=>new EmailDeliveryException(HttpStatusCode.BadRequest,"provider-request-1"),
        "throttled"=>new EmailDeliveryException((HttpStatusCode)429,"provider-request-1"),
        "provider-down"=>new EmailDeliveryException(HttpStatusCode.ServiceUnavailable,"provider-request-1"),
        "timeout"=>new TaskCanceledException("send timed out"),
        _=>new InvalidOperationException("unexpected")
    };

    // ---- Account verification email -------------------------------------------------------------

    private static (EmailVerificationWorker Worker,CapturedLog<EmailVerificationWorker> Log) Verification(IEmailVerificationQueue queue,Mail mail,bool enabled=true)
    {
        var log=new CapturedLog<EmailVerificationWorker>();
        var scopes=WorkerHarness.Scopes(s=>s.AddSingleton(queue).AddSingleton<IEmailService>(mail));
        return (new EmailVerificationWorker(scopes,Options.Create(new AuthEmailOptions{WorkerEnabled=enabled}),log),log);
    }
    private static IEmailVerificationQueue VerificationQueue(Outcome outcome,VerificationEmailJob job,string? link,bool deliverable=true,Func<Exception>? claimError=null)
        =>ResetProxy.For<IEmailVerificationQueue>((method,args)=>method switch
        {
            "ClaimAsync"=>outcome.Claim(job,claimError),
            "CreateLinkAsync"=>Task.FromResult(link),
            "CanDeliverAsync"=>Task.FromResult(deliverable),
            "CompleteAsync"=>outcome.Finish("complete"),
            "FailAsync"=>outcome.Finish("fail:"+args[1]),
            _=>throw new InvalidOperationException(method)
        });

    [Theory]
    [InlineData("send")]
    [InlineData("no-link")]      // The job's generation was superseded: nothing to send, the job is closed.
    [InlineData("undeliverable")] // The account was verified or removed meanwhile: no email, the job is closed.
    public async Task Verification_worker_sends_only_a_live_encoded_link_and_completes_the_job(string mode)
    {
        var job=new VerificationEmailJob(Guid.NewGuid(),Guid.NewGuid(),Recipient,Guid.NewGuid(),1);
        var outcome=new Outcome();var mail=new Mail();
        var (worker,_)=Verification(VerificationQueue(outcome,job,mode=="no-link"?null:"https://example.test/verify-email/tok?a=1&b=\"x\"",mode!="undeliverable"),mail);
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("complete",outcome.Result);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        if(mode!="send"){Assert.Empty(mail.Sent);return;}
        var sent=Assert.Single(mail.Sent);
        Assert.Equal(Recipient,sent.To);Assert.Equal("[FET3D] Xác minh địa chỉ email của bạn",sent.Subject);
        Assert.Contains("href=\"https://example.test/verify-email/tok?a=1&amp;b=&quot;x&quot;\"",sent.Html);
        Assert.Contains("15 phút",sent.Html);
    }

    [Theory]
    [InlineData("permanent",true)]
    [InlineData("throttled",false)]
    [InlineData("provider-down",false)]
    [InlineData("timeout",false)] // The 45 second send timeout is not a shutdown: the job is retried.
    [InlineData("other",false)]
    public async Task Verification_worker_fails_the_job_permanently_only_for_provider_client_errors(string failure,bool permanent)
    {
        var job=new VerificationEmailJob(Guid.NewGuid(),Guid.NewGuid(),Recipient,Guid.NewGuid(),2);
        var outcome=new Outcome();var mail=new Mail{Send=_=>throw Failure(failure)};
        var (worker,log)=Verification(VerificationQueue(outcome,job,"https://example.test/verify-email/secret-token"),mail);
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("fail:"+permanent,outcome.Result);
        var warning=Assert.Single(log.Entries,x=>x.Level==LogLevel.Warning);
        Assert.Contains(job.Id.ToString(),warning.Message);Assert.Contains("attempt 2",warning.Message);
        if(failure is "permanent" or "throttled" or "provider-down")Assert.Contains("ProviderRequestId=provider-request-1",warning.Message);
        Assert.DoesNotContain(Recipient,log.All());Assert.DoesNotContain("secret-token",log.All());
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Verification_worker_retries_after_a_polling_failure_and_leaves_a_job_owned_when_stopped_mid_send()
    {
        // Polling failure: logged by type only, then the worker backs off five seconds and delivers on the next claim.
        var job=new VerificationEmailJob(Guid.NewGuid(),Guid.NewGuid(),Recipient,Guid.NewGuid(),1);
        var outcome=new Outcome();var mail=new Mail();
        var (worker,log)=Verification(VerificationQueue(outcome,job,"https://example.test/verify-email/t",claimError:()=>new TimeoutException("db at 10.0.0.5 timed out")),mail);
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("Email verification queue polling failed (TimeoutException).",Assert.Single(log.Entries).Message);
        Assert.Equal("complete",outcome.Result);Assert.Single(mail.Sent);Assert.True(outcome.Claims>=2);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);

        // Shutdown while the provider call is in flight: neither completed nor failed, the lease expires and another worker retries.
        var inFlight=WorkerHarness.Signal();var stopped=new Outcome();
        var (second,secondLog)=Verification(VerificationQueue(stopped,job,"https://example.test/verify-email/t"),
            new Mail{Send=ct=>{inFlight.TrySetResult();return WorkerHarness.UntilStopped(ct);}});
        await WorkerHarness.Run(second,inFlight.Task);
        Assert.Null(stopped.Result);Assert.Empty(secondLog.Entries);Assert.True(second.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Disabled_verification_worker_never_claims()
    {
        var queue=ResetProxy.For<IEmailVerificationQueue>((method,_)=>throw new InvalidOperationException("Must not claim: "+method));
        var (worker,_)=Verification(queue,new Mail(),enabled:false);
        await worker.StartAsync(default);await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    // ---- Registration OTP email -----------------------------------------------------------------

    private static (RegistrationOtpEmailWorker Worker,CapturedLog<RegistrationOtpEmailWorker> Log) Otp(Outcome outcome,RegistrationOtpEmailJob? job,Mail mail,Func<Exception>? claimError=null)
    {
        var queue=ResetProxy.For<IRegistrationOtpDeliveryQueue>((method,args)=>method switch
        {
            "ClaimAsync"=>outcome.Claim(job,claimError),
            "CompleteAsync"=>outcome.Finish("complete"),
            "FailAsync"=>outcome.Finish("fail:"+args[1]),
            _=>throw new InvalidOperationException(method)
        });
        var log=new CapturedLog<RegistrationOtpEmailWorker>();
        return (new RegistrationOtpEmailWorker(WorkerHarness.Scopes(s=>s.AddSingleton(queue).AddSingleton<IEmailService>(mail)),Enabled,log),log);
    }

    [Fact]
    public async Task Otp_worker_sends_the_code_and_completes_the_job()
    {
        var outcome=new Outcome();var mail=new Mail();
        var (worker,_)=Otp(outcome,new RegistrationOtpEmailJob(Guid.NewGuid(),Recipient,"<12345>",Guid.NewGuid(),1),mail);
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("complete",outcome.Result);
        var sent=Assert.Single(mail.Sent);
        Assert.Equal(Recipient,sent.To);Assert.Equal("[FET3D] Mã xác minh đăng ký",sent.Subject);
        Assert.Contains("&lt;12345&gt;",sent.Html);Assert.DoesNotContain("<12345>",sent.Html);Assert.Contains("10 phút",sent.Html);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("permanent",true)]
    [InlineData("throttled",false)]
    [InlineData("timeout",false)]
    [InlineData("other",false)]
    public async Task Otp_worker_retries_unless_the_provider_rejected_the_request(string failure,bool permanent)
    {
        var job=new RegistrationOtpEmailJob(Guid.NewGuid(),Recipient,"482913",Guid.NewGuid(),3);
        var outcome=new Outcome();
        var (worker,log)=Otp(outcome,job,new Mail{Send=_=>throw Failure(failure)});
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("fail:"+permanent,outcome.Result);
        Assert.Contains(job.Id.ToString(),Assert.Single(log.Entries,x=>x.Level==LogLevel.Warning).Message);
        Assert.DoesNotContain(Recipient,log.All());Assert.DoesNotContain("482913",log.All());
    }

    [Fact]
    public async Task Otp_worker_retries_after_a_polling_failure_and_keeps_the_job_when_stopped_mid_send()
    {
        var outcome=new Outcome();var mail=new Mail();
        var (worker,log)=Otp(outcome,new RegistrationOtpEmailJob(Guid.NewGuid(),Recipient,"482913",Guid.NewGuid(),1),mail,()=>new InvalidOperationException("relation does not exist"));
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("Registration OTP email queue polling failed (InvalidOperationException).",Assert.Single(log.Entries).Message);
        Assert.Equal("complete",outcome.Result);Assert.Single(mail.Sent);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);

        var inFlight=WorkerHarness.Signal();var stopped=new Outcome();
        var (second,_)=Otp(stopped,new RegistrationOtpEmailJob(Guid.NewGuid(),Recipient,"482913",Guid.NewGuid(),1),
            new Mail{Send=ct=>{inFlight.TrySetResult();return WorkerHarness.UntilStopped(ct);}});
        await WorkerHarness.Run(second,inFlight.Task);
        Assert.Null(stopped.Result);Assert.True(second.ExecuteTask!.IsCompletedSuccessfully);
    }

    // ---- Password reset email -------------------------------------------------------------------

    private static (PasswordResetWorker Worker,CapturedLog<PasswordResetWorker> Log) Reset(Outcome outcome,ResetEmailJob? job,User? user,
        Func<Task<string?>> link,Mail mail,Func<Exception>? claimError=null)
    {
        var queue=ResetProxy.For<IPasswordResetQueue>((method,args)=>method switch
        {
            "ClaimAsync"=>outcome.Claim(job,claimError),
            "CompleteAsync"=>outcome.Finish("complete"),
            "FailAsync"=>outcome.Finish("fail:"+args[1]),
            _=>throw new InvalidOperationException(method)
        });
        var accounts=ResetProxy.For<IAuthStore>((method,_)=>method=="FindUserByEmailAsync"?Task.FromResult(user):throw new InvalidOperationException(method));
        var reset=ResetProxy.For<ILocalPasswordReset>((method,_)=>method=="CreateLinkAsync"?link():throw new InvalidOperationException(method));
        var log=new CapturedLog<PasswordResetWorker>();
        var scopes=WorkerHarness.Scopes(s=>s.AddSingleton(queue).AddSingleton(accounts).AddSingleton(reset).AddSingleton<IEmailService>(mail));
        return (new PasswordResetWorker(scopes,Enabled,log),log);
    }
    private static User Account(bool active=true,bool deleted=false)=>new(){Id=Guid.NewGuid(),Email=Recipient,IsActive=active,DeletedAt=deleted?DateTime.UtcNow:null};

    [Theory]
    [InlineData("active",1)]
    [InlineData("inactive",0)] // A disabled or deleted account never receives a link; the job closes without enumeration.
    [InlineData("deleted",0)]
    [InlineData("missing",0)]
    [InlineData("no-link",0)]  // The account lost its local password before sending.
    public async Task Reset_worker_mails_a_link_only_to_an_active_account_and_always_closes_the_job(string account,int mails)
    {
        var outcome=new Outcome();var mail=new Mail();var links=0;
        var user=account switch{"inactive"=>Account(active:false),"deleted"=>Account(deleted:true),"missing"=>null,_=>Account()};
        var (worker,_)=Reset(outcome,new ResetEmailJob(Guid.NewGuid(),Recipient,Guid.NewGuid(),1),user,
            ()=>{links++;return Task.FromResult(account=="no-link"?null:"https://example.test/reset-password?token=abc");},mail);
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("complete",outcome.Result);
        Assert.Equal(mails,mail.Sent.Count);
        Assert.Equal(account is "active" or "no-link"?1:0,links);
        if(mails==1){Assert.Equal(PasswordResetWorker.Subject,mail.Sent[0].Subject);Assert.Contains("token=abc",mail.Sent[0].Html);}
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("reset-permanent",true)]
    [InlineData("reset-transient",false)]
    [InlineData("permanent",true)]
    [InlineData("provider-down",false)]
    public async Task Reset_worker_maps_permanent_reset_and_provider_errors_to_a_permanent_failure(string failure,bool permanent)
    {
        var outcome=new Outcome();var job=new ResetEmailJob(Guid.NewGuid(),Recipient,Guid.NewGuid(),4);
        Func<Task<string?>> link=failure switch
        {
            "reset-permanent"=>()=>throw new PasswordResetException("RESET_NOT_AVAILABLE","unavailable",400,true),
            "reset-transient"=>()=>throw PasswordResetException.Unavailable(),
            _=>()=>Task.FromResult<string?>("https://example.test/reset-password?token=secret-link")
        };
        var (worker,log)=Reset(outcome,job,Account(),link,new Mail{Send=_=>throw Failure(failure)});
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("fail:"+permanent,outcome.Result);
        var warning=Assert.Single(log.Entries,x=>x.Level==LogLevel.Warning).Message;
        Assert.Contains(job.Id.ToString(),warning);Assert.Contains("attempt 4",warning);
        Assert.DoesNotContain(Recipient,log.All());Assert.DoesNotContain("secret-link",log.All());
    }

    [Fact]
    public async Task Reset_worker_retries_after_a_polling_failure_and_keeps_the_job_when_stopped_mid_send()
    {
        var outcome=new Outcome();var mail=new Mail();
        var (worker,log)=Reset(outcome,new ResetEmailJob(Guid.NewGuid(),Recipient,Guid.NewGuid(),1),Account(),
            ()=>Task.FromResult<string?>("https://example.test/reset-password?token=abc"),mail,()=>new TimeoutException());
        await WorkerHarness.Run(worker,outcome.Done.Task);
        Assert.Equal("Password reset queue polling failed (TimeoutException).",Assert.Single(log.Entries).Message);
        Assert.Equal("complete",outcome.Result);Assert.Single(mail.Sent);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);

        // Shutdown during the back-off after a polling failure ends the loop without another claim.
        var backingOff=new Outcome();var logged=WorkerHarness.Signal();
        var (idle,idleLog)=Reset(backingOff,null,null,()=>Task.FromResult<string?>(null),new Mail(),()=>new TimeoutException());
        idleLog.Logged=(_,_)=>logged.TrySetResult();
        await WorkerHarness.Run(idle,logged.Task);
        Assert.Equal(1,backingOff.Claims);Assert.True(idle.ExecuteTask!.IsCompletedSuccessfully);

        var inFlight=WorkerHarness.Signal();var stopped=new Outcome();
        var (second,_)=Reset(stopped,new ResetEmailJob(Guid.NewGuid(),Recipient,Guid.NewGuid(),1),Account(),
            ()=>Task.FromResult<string?>("https://example.test/reset-password?token=abc"),
            new Mail{Send=ct=>{inFlight.TrySetResult();return WorkerHarness.UntilStopped(ct);}});
        await WorkerHarness.Run(second,inFlight.Task);
        Assert.Null(stopped.Result);Assert.True(second.ExecuteTask!.IsCompletedSuccessfully);
    }
}
