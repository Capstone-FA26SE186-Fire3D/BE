using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class MaintenanceWorkerLoopTests
{
    // ---- Refresh-token cleanup -----------------------------------------------------------------

    private static (RefreshTokenCleanupWorker Worker,CapturedLog<RefreshTokenCleanupWorker> Log) Cleanup(AuthTokenCleanupOptions options,
        Func<int,int,CancellationToken,Task<int>> delete,TimeSpan? startupDelay=null)
    {
        var store=ResetProxy.For<IRefreshTokenCleanupStore>((method,args)=>method=="DeleteExpiredFamiliesAsync"
            ?delete((int)args[0]!,(int)args[1]!,(CancellationToken)args[2]!):throw new InvalidOperationException(method));
        var log=new CapturedLog<RefreshTokenCleanupWorker>();
        var worker=new RefreshTokenCleanupWorker(WorkerHarness.Scopes(s=>s.AddScoped(_=>store)),Options.Create(options),log)
            {StartupDelay=startupDelay??TimeSpan.FromSeconds(45)};
        return (worker,log);
    }

    [Fact]
    public async Task Refresh_cleanup_run_deletes_in_batches_until_empty_and_caps_the_batches()
    {
        var calls=new List<(int Retention,int Batch)>();var results=new Queue<int>([500,500,3,0]);
        var (worker,log)=Cleanup(new AuthTokenCleanupOptions{RetentionDays=9,BatchSize=500,MaxBatchesPerRun=10},
            (retention,batch,_)=>{calls.Add((retention,batch));return Task.FromResult(results.Dequeue());});
        Assert.Equal(1003,await worker.RunOnceAsync(default));
        Assert.Equal(Enumerable.Repeat((9,500),4),calls);
        Assert.Contains("Deleted 1003 expired refresh tokens.",log.All());

        // A backlog larger than one run is bounded by MaxBatchesPerRun; the next run continues.
        calls.Clear();
        var (capped,_)=Cleanup(new AuthTokenCleanupOptions{BatchSize=100,MaxBatchesPerRun=2},(retention,batch,_)=>{calls.Add((retention,batch));return Task.FromResult(100);});
        Assert.Equal(200,await capped.RunOnceAsync(default));
        Assert.Equal(2,calls.Count);

        // Nothing expired: no batch beyond the first and nothing logged.
        var (idle,idleLog)=Cleanup(new AuthTokenCleanupOptions(),(_,_,_)=>Task.FromResult(0));
        Assert.Equal(0,await idle.RunOnceAsync(default));Assert.Empty(idleLog.Entries);
        var (disabled,_)=Cleanup(new AuthTokenCleanupOptions{Enabled=false},(_,_,_)=>throw new InvalidOperationException("Must not delete"));
        Assert.Equal(0,await disabled.RunOnceAsync(default));
    }

    [Fact]
    public async Task Disabled_refresh_cleanup_worker_logs_and_never_runs()
    {
        var (worker,log)=Cleanup(new AuthTokenCleanupOptions{Enabled=false},(_,_,_)=>throw new InvalidOperationException("Must not delete"),TimeSpan.Zero);
        await worker.StartAsync(default);await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));await worker.StopAsync(default);
        Assert.Equal("Refresh-token cleanup worker is disabled.",Assert.Single(log.Entries).Message);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]   // A failed run is logged and the worker keeps its schedule.
    [InlineData("shutdown")]  // Stopping during a run ends the loop without an error.
    public async Task Refresh_cleanup_worker_runs_after_the_startup_delay_and_survives_failures(string mode)
    {
        var step=WorkerHarness.Signal();
        var (worker,log)=Cleanup(new AuthTokenCleanupOptions{IntervalMinutes=60},async (_,_,ct)=>
        {
            if(mode=="shutdown"){step.TrySetResult();await WorkerHarness.UntilStopped(ct);}
            if(mode=="failure")throw new InvalidOperationException("cleanup gate missing");
            step.TrySetResult();return 0;
        },TimeSpan.Zero);
        if(mode=="failure")log.Logged=(level,_)=>{if(level==LogLevel.Error)step.TrySetResult();};
        await worker.StartAsync(default);
        try{await step.Task.WaitAsync(TimeSpan.FromSeconds(10));}
        finally{await worker.StopAsync(default);}
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        if(mode=="failure")
        {
            var error=Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Error,error.Level);Assert.IsType<InvalidOperationException>(error.Exception);
        }
        else Assert.DoesNotContain(log.Entries,x=>x.Level==LogLevel.Error);
    }

    // ---- PayOS recovery ------------------------------------------------------------------------

    private static (PayosRecoveryWorker Worker,CapturedLog<PayosRecoveryWorker> Log) Payos(PayosOptions options,Func<int,CancellationToken,Task> recover)
    {
        var calls=0;
        var payments=ResetProxy.For<IPayosPayments>((method,args)=>method=="Recover"
            ?recover(Interlocked.Increment(ref calls),(CancellationToken)args[0]!):throw new InvalidOperationException("Must only recover: "+method));
        var log=new CapturedLog<PayosRecoveryWorker>();
        return (new PayosRecoveryWorker(WorkerHarness.Scopes(s=>s.AddScoped(_=>payments)),Options.Create(options),log),log);
    }

    [Fact]
    public async Task Payos_recovery_retries_on_the_next_tick_after_a_failed_batch()
    {
        var secondBatch=WorkerHarness.Signal();
        var (worker,log)=Payos(new PayosOptions{WorkerEnabled=true,Enabled=true,PollSeconds=1},(call,_)=>
        {
            if(call==1)throw new HttpRequestException("payos.vn unreachable");
            secondBatch.TrySetResult();return Task.CompletedTask;
        });
        await WorkerHarness.Run(worker,secondBatch.Task);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Contains("PayOS recovery worker enabled; checkout enabled True, interval 1s",log.All());
        var warning=Assert.Single(log.Entries,x=>x.Level==LogLevel.Warning);
        Assert.Equal("PayOS recovery batch failed (HttpRequestException); will retry",warning.Message);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task Payos_recovery_stops_cleanly_mid_batch_and_does_nothing_when_disabled()
    {
        var inFlight=WorkerHarness.Signal();
        var (worker,log)=Payos(new PayosOptions{WorkerEnabled=true,PollSeconds=30},async (_,ct)=>{inFlight.TrySetResult();await WorkerHarness.UntilStopped(ct);});
        await WorkerHarness.Run(worker,inFlight.Task);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.DoesNotContain(log.Entries,x=>x.Level==LogLevel.Warning);

        var (disabled,disabledLog)=Payos(new PayosOptions{WorkerEnabled=false},(_,_)=>throw new InvalidOperationException("Must not recover"));
        await disabled.StartAsync(default);await disabled.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));await disabled.StopAsync(default);
        Assert.Equal("PayOS recovery worker is disabled.",Assert.Single(disabledLog.Entries).Message);
    }

    // ---- Pending registration cleanup ------------------------------------------------------------

    // A disposable database whose cleanup function stands in for the real gate: it can deny, fail or block.
    private sealed class CleanupDatabase : IAsyncDisposable
    {
        private readonly string admin=Environment.GetEnvironmentVariable("FIRE3D_RESET_TEST_ADMIN")!;
        private readonly string name="fire3d_cleanup_loop_"+Guid.NewGuid().ToString("N");
        public string Connection="";
        public static async Task<CleanupDatabase> Create(string mode)
        {
            var db=new CleanupDatabase();var settings=new NpgsqlConnectionStringBuilder(db.admin);
            if(settings.Host is not ("localhost" or "127.0.0.1" or "::1"))throw new InvalidOperationException("Integration tests require loopback PostgreSQL.");
            await using(var c=new NpgsqlConnection(db.admin)){await c.OpenAsync();await new NpgsqlCommand($"CREATE DATABASE {db.name}",c).ExecuteNonQueryAsync();}
            settings.Database=db.name;db.Connection=settings.ConnectionString;
            await db.Sql($"""
                CREATE TABLE cleanup_mode(mode text NOT NULL); INSERT INTO cleanup_mode VALUES('{mode}');
                CREATE FUNCTION public.cleanup_pending_registrations(batch integer) RETURNS integer LANGUAGE plpgsql AS $$
                DECLARE m text; BEGIN
                  SELECT cleanup_mode.mode INTO m FROM cleanup_mode;
                  IF m='deny' THEN RAISE EXCEPTION 'permission denied for function cleanup_pending_registrations' USING ERRCODE='insufficient_privilege'; END IF;
                  IF m='fail' THEN RAISE EXCEPTION 'cleanup failed' USING ERRCODE='P0001'; END IF;
                  IF m='block' THEN PERFORM pg_sleep(60); END IF;
                  RETURN 0;
                END $$;
                """);
            return db;
        }
        public Fire3DDbContext Context()
        {
            var services=new ServiceCollection();
            services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:DefaultConnection"]=Connection}).Build());
            return new Fire3DDbContext(services.BuildServiceProvider().GetRequiredService<DbContextOptions<Fire3DDbContext>>());
        }
        public async Task<object?> Sql(string sql)
        {
            await using var c=new NpgsqlConnection(Connection);await c.OpenAsync();
            return await new NpgsqlCommand(sql,c).ExecuteScalarAsync();
        }
        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var c=new NpgsqlConnection(admin);await c.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)",c).ExecuteNonQueryAsync();
        }
    }

    [ResetPostgresFact]
    public async Task Pending_cleanup_worker_reports_a_missing_grant_separately_from_other_failures()
    {
        foreach(var mode in new[]{"deny","fail"})
        {
            await using var db=await CleanupDatabase.Create(mode);
            var log=new CapturedLog<PendingRegistrationCleanupWorker>();var logged=WorkerHarness.Signal();
            log.Logged=(level,_)=>{if(level==LogLevel.Error)logged.TrySetResult();};
            var worker=new PendingRegistrationCleanupWorker(WorkerHarness.Scopes(s=>s.AddScoped(_=>db.Context())),log);
            await WorkerHarness.Run(worker,logged.Task);
            Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
            var error=Assert.Single(log.Entries);
            if(mode=="deny")
            {
                // The permission problem is an operator action: SQLSTATE only, no exception text or stack.
                Assert.Equal("Pending registration cleanup blocked. SQLSTATE=42501; retry in 60 seconds.",error.Message);
                Assert.Null(error.Exception);
            }
            else
            {
                Assert.Equal("Expired registration cleanup failed.",error.Message);
                Assert.Equal("P0001",Assert.IsType<PostgresException>(error.Exception).SqlState);
            }
        }
    }

    [ResetPostgresFact]
    public async Task Pending_cleanup_worker_cancels_a_running_cleanup_on_shutdown()
    {
        await using var db=await CleanupDatabase.Create("block");
        var log=new CapturedLog<PendingRegistrationCleanupWorker>();
        var worker=new PendingRegistrationCleanupWorker(WorkerHarness.Scopes(s=>s.AddScoped(_=>db.Context())),log);
        await worker.StartAsync(default);
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while((long)(await db.Sql("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND state='active' AND query LIKE '%cleanup_pending_registrations(100)%'"))! == 0)
            {Assert.True(DateTime.UtcNow<deadline,"The cleanup call never started.");await Task.Delay(50);}
        }
        finally{await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(15));}
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(log.Entries);
    }
}
