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
}
