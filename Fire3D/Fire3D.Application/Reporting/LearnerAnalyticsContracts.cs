using System.Text.Json;
using Fire3D.Application.Authentication;
namespace Fire3D.Application.Reporting;
public sealed class LearnerAnalyticsOptions
{
    public const string Section="Analytics";
    /// <summary>A Running session counts as active when the server received a heartbeat within this window.</summary>
    public int ActiveSessionSeconds { get; init; }=120;
}
/// <summary>Training (organization/platform) and revenue analytics computed by the database in one read-only RepeatableRead snapshot.</summary>
public interface ILearnerAnalytics
{
    Task<AuthResult<JsonElement>> Read(string report,Guid actor,Guid family,string? from,string? to,CancellationToken ct);
    /// <summary>PlatformAdmin training and AI analytics of one organization (inactive included, deleted 404).</summary>
    Task<AuthResult<JsonElement>> ReadOrganization(Guid actor,Guid family,Guid organizationId,string? from,string? to,CancellationToken ct);
    /// <summary>Training report with filters and cohorts (#80), read in one read-only snapshot.</summary>
    Task<AuthResult<JsonElement>> ReadTraining(Guid actor,Guid family,TrainingAnalyticsQuery query,CancellationToken ct)=>throw new NotSupportedException();
    /// <summary>Synchronous CSV export of the cohort rows with the same filters; counts toward the daily limit and is audited.</summary>
    Task<AuthResult<TrainingAnalyticsExport>> ExportTraining(Guid actor,Guid family,TrainingAnalyticsQuery query,CancellationToken ct)=>throw new NotSupportedException();
}

/// <summary>Scope: Organization (caller's tenant), Platform (all tenants), PlatformOrganization (one tenant for PlatformAdmin).
/// GroupBy: day, week (Monday, UTC), month, building or scenario of the session start.</summary>
public sealed record TrainingAnalyticsQuery(string Scope, Guid? OrganizationId, Guid? BuildingId, Guid? ScenarioId, string? GroupBy, string? From, string? To);
public sealed record TrainingAnalyticsExport(string FileName, byte[] Content, int Rows);

public sealed record AnalyticsFilters(Guid? BuildingId, Guid? ScenarioId, string? GroupBy);
public sealed record AnalyticsOrganizationRef(Guid Id, string Name, bool IsActive);
public sealed record AnalyticsAssessment(long Assessed, long Passed, decimal? PassRate);
public sealed record AnalyticsDuration(long Sessions, decimal? AverageSeconds, decimal? MedianSeconds);
public sealed record AnalyticsModeRow(string Mode, long Plays, long Completed);
public sealed record AnalyticsTrainingRow(Guid TrainingId, string? TrainingName, long Plays, long Completed, long Passed);
public sealed record AnalyticsBuildingRow(Guid BuildingId, string? BuildingName, long Plays, long UniqueTrainees, long Completed);
public sealed record AnalyticsScenarioRow(Guid ScenarioId, string? ScenarioName, long Plays, long UniqueTrainees, long Completed, long Passed);
public sealed record AnalyticsOrganizationRow(Guid OrganizationId, string? OrganizationName, long Plays, long UniqueTrainees, long Completed);
/// <summary>One cohort group. UniqueTrainees counts inside this group only; never add groups to get a total.</summary>
public sealed record AnalyticsCohortRow(string Key, string? Label, long Plays, long UniqueTrainees, long Completed, long Abandoned, long AwaitingSync,
    long Assessed, long Passed, decimal? PassRate, decimal? AverageDurationSeconds);
/// <summary>Assessment criterion results stored by the server, aggregated; no individual answers.</summary>
public sealed record AnalyticsDebriefRow(string CriterionId, string Metric, long Evaluated, long Passed, decimal PassRate);
public sealed record AnalyticsQuotaUnits(string QuotaUnit, long Units);
public sealed record AnalyticsReconcile(long Requests, long ReservedUnits);
public sealed record AnalyticsAiUsage(IReadOnlyList<AnalyticsQuotaUnits> SettledUnits, IReadOnlyList<AnalyticsQuotaUnits> OpenReservations,
    AnalyticsReconcile NeedsReconcile, IReadOnlyDictionary<string, long> Requests);
/// <summary>
/// Training report. Completed excludes Abandoned sessions; Abandoned and AwaitingSync are separate. Duration is the runtime's
/// completion_time_seconds. ByOrganization only for the platform-wide report; Cohorts only with groupBy; Organization only for
/// the PlatformAdmin view of one tenant. Rates without samples are null.
/// </summary>
public sealed record TrainingAnalyticsReport(DateTime AsOf, DateTime From, DateTime To, AnalyticsOrganizationRef? Organization, AnalyticsFilters Filters,
    long Plays, long UniqueTrainees, long Completed, long Abandoned, long AwaitingSync, decimal? CompletionRate, long ActiveSessions,
    IReadOnlyDictionary<string, long> Outcomes, AnalyticsAssessment Assessment, AnalyticsDuration Duration, IReadOnlyList<AnalyticsModeRow> ByMode,
    IReadOnlyList<AnalyticsTrainingRow> ByTraining, IReadOnlyList<AnalyticsBuildingRow> ByBuilding, IReadOnlyList<AnalyticsScenarioRow> ByScenario,
    IReadOnlyList<AnalyticsOrganizationRow>? ByOrganization, IReadOnlyList<AnalyticsCohortRow>? Cohorts, IReadOnlyList<AnalyticsDebriefRow> Debrief,
    AnalyticsAiUsage AiUsage, int ActiveWindowSeconds, IReadOnlyDictionary<string, string> Definitions);

/// <summary>CSV of cohort rows: UTF-8 with BOM for Excel, fixed header, cells that could start a formula are prefixed with an apostrophe.</summary>
public static class TrainingAnalyticsCsv
{
    public static readonly string[] Header = ["groupBy", "key", "label", "plays", "uniqueTrainees", "completed", "abandoned", "awaitingSync", "assessed", "passed", "passRate", "averageDurationSeconds"];

    public static byte[] Build(string groupBy, IEnumerable<AnalyticsCohortRow> rows)
    {
        var text = new System.Text.StringBuilder();
        text.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in rows)
        {
            string N(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            text.Append(string.Join(',', new[] { groupBy, r.Key, r.Label ?? "", r.Plays.ToString(), r.UniqueTrainees.ToString(), r.Completed.ToString(),
                r.Abandoned.ToString(), r.AwaitingSync.ToString(), r.Assessed.ToString(), r.Passed.ToString(), N(r.PassRate), N(r.AverageDurationSeconds) }.Select(Cell))).Append("\r\n");
        }
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Neutralises spreadsheet formulas (= + - @, tab, CR at the start) and quotes cells with separators or quotes.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
