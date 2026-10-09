using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Releases;

public sealed record BuildReleaseRequest(
    Guid RevisionId,
    Guid ScenarioVersionId,
    Guid ConfirmationReviewId,
    Guid CandidateArtifactId,
    string? SafetyThresholds = null,
    string? ManifestUrl = null,
    string? ManifestSha256 = null,
    string? PackageUrl = null,
    string? ChecksumSha256 = null,
    long? PackageSizeBytes = null,
    string? MinRuntimeVersion = null,
    string? SchemaVersion = null,
    string? BuildTarget = null);

public sealed record RevokeReleaseRequest(string Reason);

public sealed record ReleasePackageResponse(
    Guid Id,
    Guid CandidateArtifactId,
    string ManifestUrl,
    string ManifestSha256,
    string PackageUrl,
    string ChecksumSha256,
    long PackageSizeBytes,
    string MinRuntimeVersion,
    string SchemaVersion,
    string BuildTarget);

public sealed record ReleaseResponse(
    Guid Id,
    Guid RevisionId,
    Guid ScenarioVersionId,
    Guid BuildingId,
    Guid OrganizationId,
    Guid ConfirmationReviewId,
    string Status,
    string SafetyThresholds,
    Guid? PublishedBy,
    DateTime? PublishedAt,
    Guid? RevokedBy,
    string? RevokedReason,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    ReleasePackageResponse Package);

public interface IReleaseStore
{
    Task<AuthResult<ReleaseResponse>> BuildAsync(Guid actorId, Guid? organizationId, BuildReleaseRequest request, CancellationToken ct, string? key = null, Guid? family = null);
    Task<ReleaseResponse?> GetAsync(Guid releaseId, Guid? organizationId, CancellationToken ct);
    Task<AuthResult<ReleaseResponse>> PublishAsync(Guid actorId, Guid releaseId, Guid? organizationId, CancellationToken ct, Guid? family = null, string? key = null);
    Task<AuthResult<bool>> RevokeAsync(Guid actorId, Guid releaseId, Guid? organizationId, string reason, CancellationToken ct, Guid? family = null);
}

public sealed record BuildReleaseCommand(Guid ActorId, BuildReleaseRequest Request, string? Key = null, Guid? Family = null) : IRequest<AuthResult<ReleaseResponse>>;
public sealed record GetReleaseQuery(Guid ActorId, Guid ReleaseId) : IRequest<AuthResult<ReleaseResponse>>;
public sealed record RevokeReleaseCommand(Guid ActorId, Guid ReleaseId, RevokeReleaseRequest Request, Guid? Family = null) : IRequest<AuthResult<bool>>;

public sealed class BuildReleaseHandler(IAuthStore accounts, IReleaseStore store)
    : IRequestHandler<BuildReleaseCommand, AuthResult<ReleaseResponse>>
{
    public async Task<AuthResult<ReleaseResponse>> Handle(BuildReleaseCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (!ReleaseValidation.IsValid(command.Request))
            return AuthResult<ReleaseResponse>.Fail("VALIDATION_ERROR", "Release package metadata is invalid.", 400);
        return await store.BuildAsync(command.ActorId, scope.Value!.OrganizationId, command.Request, ct, command.Key, command.Family);
    }
}

public sealed class GetReleaseHandler(IAuthStore accounts, IReleaseStore store)
    : IRequestHandler<GetReleaseQuery, AuthResult<ReleaseResponse>>
{
    public async Task<AuthResult<ReleaseResponse>> Handle(GetReleaseQuery query, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, query.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (query.ReleaseId == Guid.Empty)
            return AuthResult<ReleaseResponse>.Fail("VALIDATION_ERROR", "Release id is required.", 400);
        var release = await store.GetAsync(query.ReleaseId, scope.Value!.OrganizationId, ct);
        return release is null
            ? AuthResult<ReleaseResponse>.Fail("NOT_FOUND", "Release not found or access denied.", 404)
            : AuthResult<ReleaseResponse>.Ok(release);
    }
}

public sealed class RevokeReleaseHandler(IAuthStore accounts, IReleaseStore store)
    : IRequestHandler<RevokeReleaseCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(RevokeReleaseCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        var reason = command.Request?.Reason?.Trim();
        if (command.ReleaseId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return AuthResult<bool>.Fail("VALIDATION_ERROR", "A revoke reason between 1 and 1000 characters is required.", 400);
        return await store.RevokeAsync(command.ActorId, command.ReleaseId, scope.Value!.OrganizationId, reason, ct, command.Family);
    }
}

internal static partial class ReleaseValidation
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public static bool IsValid(BuildReleaseRequest? request)
    {
        if (request is null || request.RevisionId == Guid.Empty || request.ScenarioVersionId == Guid.Empty
            || request.ConfirmationReviewId == Guid.Empty || request.CandidateArtifactId == Guid.Empty
            || request.PackageSizeBytes is <= 0
            || request.ManifestSha256 is not null && !Sha256Regex().IsMatch(request.ManifestSha256)
            || request.ChecksumSha256 is not null && !Sha256Regex().IsMatch(request.ChecksumSha256)
            || !OptionalText(request.ManifestUrl,2048) || !OptionalText(request.PackageUrl,2048)
            || !OptionalText(request.MinRuntimeVersion,50) || !OptionalText(request.SchemaVersion,50)
            || !OptionalText(request.BuildTarget,50)) return false;
        if(request.SafetyThresholds is null)return true;
        if(!ValidText(request.SafetyThresholds,20000))return false;
        try{using var json=JsonDocument.Parse(request.SafetyThresholds);return json.RootElement.ValueKind==JsonValueKind.Object;}
        catch(JsonException){return false;}
    }
    private static bool OptionalText(string? value,int max)=>value is null || ValidText(value,max);

    private static bool ValidText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
}
