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
        ["completion"]="Cohort sessions with a server-accepted result at asOf, excluding Abandoned (reported as abandoned); a late offline result counts for the cohort of the session start.",
        ["awaitingSync"]="Cohort sessions whose completion was requested but whose result is not yet stored at asOf.",
        ["cohorts"]="groupBy day, week (Monday start) or month of the UTC session start, or the Building/scenario; uniqueTrainees counts inside each group and groups are never summed.",
        ["debrief"]="Stored Assessment criterion results per criterion and metric; no individual answers.",
        ["export"]="CSV of the cohort rows with the same filters and definitions; at most 10000 rows and 10 successful exports per account per UTC day.",
        ["outcomes"]="Server result outcome Passed/NotPassed/Incomplete/NotAssessed; passRate uses Passed/(Passed+NotPassed).",
        ["duration"]="Runtime completion_time_seconds of completed cohort sessions (elapsed play time), so offline sync delay is never counted.",
        ["aiUsage"]="Settled AI units in range; open reservations and NeedsReconcile reported separately at asOf. requests counts requests created in range by v7 status (Accepted, Processing, Succeeded, Failed, NeedsReconcile, Rejected); resultTypes splits Succeeded by result type; byAudience (organization/trainee) only in the platform-wide report.",
        ["revenue"]="Applied payment transactions by receipt time in range, per currency; quotation totals are not revenue.",
        ["rates"]="A rate without samples is null, not 0.",
        ["consistency"]="Authorization and every aggregate share one read-only RepeatableRead PostgreSQL snapshot."
    };
    public Task<AuthResult<JsonElement>> Read(string report,Guid actor,Guid family,string? from,string? to,CancellationToken ct)=>
        Query("SELECT analytics_gate(@report,@actor,@family,@from,@to,@active)::text",command=>command.Parameters.AddWithValue("report",report),actor,family,from,to,ct);
    public Task<AuthResult<JsonElement>> ReadOrganization(Guid actor,Guid family,Guid organizationId,string? from,string? to,CancellationToken ct)=>
        Query("SELECT analytics_organization_gate(@actor,@family,@organization,@from,@to,@active)::text",command=>command.Parameters.AddWithValue("organization",NpgsqlDbType.Uuid,organizationId),actor,family,from,to,ct);
    public Task<AuthResult<JsonElement>> ReadTraining(Guid actor,Guid family,TrainingAnalyticsQuery query,CancellationToken ct)=>Training(actor,family,query,false,ct);

    public async Task<AuthResult<TrainingAnalyticsExport>> ExportTraining(Guid actor,Guid family,TrainingAnalyticsQuery query,CancellationToken ct)
    {
        var result=await Training(actor,family,query,true,ct);
        if(!result.IsSuccess)return new(default,result.Error);
        var report=result.Value.Deserialize<TrainingAnalyticsReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var groupBy=query.GroupBy??"day";
        var rows=report.Cohorts??[];
        var name=$"training-{query.Scope.ToLowerInvariant()}-{groupBy}-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv";
        return AuthResult<TrainingAnalyticsExport>.Ok(new(name,TrainingAnalyticsCsv.Build(groupBy,rows),rows.Count));
    }

    private Task<AuthResult<JsonElement>> Training(Guid actor,Guid family,TrainingAnalyticsQuery query,bool export,CancellationToken ct)=>
        Query("SELECT analytics_training_gate(@actor,@family,@input)::text",command=>{},actor,family,query.From,query.To,ct,(range,active)=>new
        {
            scope=query.Scope,organizationId=query.OrganizationId,buildingId=query.BuildingId,scenarioId=query.ScenarioId,groupBy=query.GroupBy,
            from=range.From,to=range.To,activeSeconds=active,export
        },readOnly:!export);

    private async Task<AuthResult<JsonElement>> Query(string sql,Action<NpgsqlCommand> bind,Guid actor,Guid family,string? from,string? to,CancellationToken ct,
        Func<(DateTime From,DateTime To),int,object>? input=null,bool readOnly=true)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        if(readOnly)await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",ct);
        var asOf=await db.Database.SqlQueryRaw<DateTime>("SELECT transaction_timestamp() AS \"Value\"").SingleAsync(ct);
        if(!ReportingRange.TryParse(from,to,asOf,out var range))
            return AuthResult<JsonElement>.Fail("VALIDATION_ERROR","Use timezone-qualified timestamps and a positive range of at most 90 days.",400);
        await using var command=new NpgsqlCommand(sql,(NpgsqlConnection)db.Database.GetDbConnection(),(NpgsqlTransaction)tx.GetDbTransaction());
        bind(command);
        command.Parameters.AddWithValue("actor",NpgsqlDbType.Uuid,actor);
        command.Parameters.AddWithValue("family",NpgsqlDbType.Uuid,family);
        if(input is not null)
            command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input((DateTime.SpecifyKind(range.From,DateTimeKind.Utc),DateTime.SpecifyKind(range.To,DateTimeKind.Utc)),options.Value.ActiveSessionSeconds)));
        else
        {
            command.Parameters.AddWithValue("from",NpgsqlDbType.TimestampTz,DateTime.SpecifyKind(range.From,DateTimeKind.Utc));
            command.Parameters.AddWithValue("to",NpgsqlDbType.TimestampTz,DateTime.SpecifyKind(range.To,DateTimeKind.Utc));
            command.Parameters.AddWithValue("active",options.Value.ActiveSessionSeconds);
        }
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        await tx.CommitAsync(ct);
        var value=document.RootElement;
        var code=value.GetProperty("code").GetString()!;
        if(code!="OK")return new(default,new AuthError(code,code switch
        {
            "FORBIDDEN"=>"This session cannot read these analytics.",
            "UNAUTHORIZED"=>"The session is no longer active.",
            "NOT_FOUND"=>"The organization, Building or scenario was not found in this report's scope.",
            "EXPORT_ROW_LIMIT_EXCEEDED"=>$"The export has {value.GetProperty("rows").GetInt32()} rows; narrow the range or grouping to at most 10000.",
            "EXPORT_DAILY_LIMIT_EXCEEDED"=>"At most 10 exports per account per UTC day.",
            _=>"Analytics request was rejected."
        },value.GetProperty("status").GetInt32(),RetryAfterSeconds:value.TryGetProperty("retryAfterSeconds",out var retry)?retry.GetInt32():null));
        var body=JsonNode.Parse(value.GetProperty("result").GetRawText())!.AsObject();
        body["activeWindowSeconds"]=options.Value.ActiveSessionSeconds;
        body["definitions"]=JsonSerializer.SerializeToNode(Definitions);
        return AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(body));
    }
}
