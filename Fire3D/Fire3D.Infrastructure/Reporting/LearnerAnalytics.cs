using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Reporting;
public sealed class LearnerAnalytics(Fire3DDbContext db,IOptions<LearnerAnalyticsOptions> options) : ILearnerAnalytics
{
    public static readonly IReadOnlyDictionary<string,string> Definitions=new Dictionary<string,string>
    {
        ["plays"]="Contract-7 learner sessions started in UTC [from,to); prepare and playtest are not counted.",
        ["uniqueTrainees"]="Distinct Trainees of those started sessions.",
        ["activeSessions"]="Running sessions with a server-received heartbeat within activeWindowSeconds before asOf; independent of the range.",
        ["completion"]="Cohort sessions with a server-accepted result at asOf; a late offline result counts for the cohort of the session start.",
        ["outcomes"]="Server result outcome Passed/NotPassed/Incomplete/NotAssessed; passRate uses Passed/(Passed+NotPassed).",
        ["duration"]="ended_at - launched_at of completed cohort sessions, server timestamps; never derived from processing jobs.",
        ["aiUsage"]="Settled AI units in range; open reservations and NeedsReconcile reported separately at asOf.",
        ["revenue"]="Applied payment transactions by receipt time in range, per currency; quotation totals are not revenue.",
        ["rates"]="A rate without samples is null, not 0.",
        ["consistency"]="Authorization and every aggregate share one read-only RepeatableRead PostgreSQL snapshot."
    };
    public async Task<AuthResult<JsonElement>> Read(string report,Guid actor,Guid family,string? from,string? to,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",ct);
        var asOf=await db.Database.SqlQueryRaw<DateTime>("SELECT transaction_timestamp() AS \"Value\"").SingleAsync(ct);
        if(!ReportingRange.TryParse(from,to,asOf,out var range))
            return AuthResult<JsonElement>.Fail("VALIDATION_ERROR","Use timezone-qualified timestamps and a positive range of at most 90 days.",400);
        await using var command=new NpgsqlCommand("SELECT analytics_gate(@report,@actor,@family,@from,@to,@active)::text",(NpgsqlConnection)db.Database.GetDbConnection(),(NpgsqlTransaction)tx.GetDbTransaction());
        command.Parameters.AddWithValue("report",report);
        command.Parameters.AddWithValue("actor",NpgsqlDbType.Uuid,actor);
        command.Parameters.AddWithValue("family",NpgsqlDbType.Uuid,family);
        command.Parameters.AddWithValue("from",NpgsqlDbType.TimestampTz,DateTime.SpecifyKind(range.From,DateTimeKind.Utc));
        command.Parameters.AddWithValue("to",NpgsqlDbType.TimestampTz,DateTime.SpecifyKind(range.To,DateTimeKind.Utc));
        command.Parameters.AddWithValue("active",options.Value.ActiveSessionSeconds);
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        await tx.CommitAsync(ct);
        var value=document.RootElement;
        var code=value.GetProperty("code").GetString()!;
        if(code!="OK")return AuthResult<JsonElement>.Fail(code,code switch
        {
            "FORBIDDEN"=>"This session cannot read these analytics.",
            "UNAUTHORIZED"=>"The session is no longer active.",
            _=>"Analytics request was rejected."
        },value.GetProperty("status").GetInt32());
        var body=JsonNode.Parse(value.GetProperty("result").GetRawText())!.AsObject();
        body["activeWindowSeconds"]=options.Value.ActiveSessionSeconds;
        body["definitions"]=JsonSerializer.SerializeToNode(Definitions);
        return AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(body));
    }
}
