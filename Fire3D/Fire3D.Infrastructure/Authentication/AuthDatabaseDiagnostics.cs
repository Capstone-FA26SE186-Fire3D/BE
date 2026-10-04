using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>Opt-in EF command duration. No SQL text or parameter values are read.</summary>
public sealed class AuthDatabaseDiagnostics(ILogger<AuthDatabaseDiagnostics> logger) : DbCommandInterceptor
{
    private static readonly Meter Meter=new("FET3D.Database");
    private static readonly Histogram<double> Duration=Meter.CreateHistogram<double>("fet3d.db.command.duration","ms");
    private void Record(CommandExecutedEventData data,string kind)
    {
        Duration.Record(data.Duration.TotalMilliseconds,new KeyValuePair<string,object?>("kind",kind));
        logger.LogDebug("FET3D database command completed: Kind={Kind}, DurationMs={DurationMs}",kind,data.Duration.TotalMilliseconds);
    }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
    {Record(data,"reader");return ValueTask.FromResult(result);}
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
    {Record(data,"nonquery");return ValueTask.FromResult(result);}
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command,CommandExecutedEventData data,object? result,CancellationToken ct=default)
    {Record(data,"scalar");return ValueTask.FromResult(result);}
}
