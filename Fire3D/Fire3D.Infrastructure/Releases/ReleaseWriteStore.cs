using Fire3D.Application.Authentication;
using Fire3D.Application.Releases;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Releases;

public sealed class ReleaseWriteStore(Fire3DDbContext db, TimeProvider clock) : IReleaseStore
{
    public async Task<AuthResult<ReleaseResponse>> BuildAsync(
        Guid actorId, Guid? organizationId, BuildReleaseRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var version = await db.ScenarioVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.ScenarioVersionId && x.RevisionId == request.RevisionId, ct);
        if (version is null || (organizationId.HasValue && version.OrganizationId != organizationId.Value))
            return AuthResult<ReleaseResponse>.Fail("NOT_FOUND", "Scenario version or revision not found.", 404);

        var revisionReady = await db.Revisions.AsNoTracking().AnyAsync(x =>
            x.Id == request.RevisionId && x.BuildingId == version.BuildingId
            && x.OrganizationId == version.OrganizationId && x.Status == RevisionStatus.ConfirmedForTraining, ct);
        var buildingActive = await db.Buildings.AsNoTracking().AnyAsync(x =>
            x.Id == version.BuildingId && x.OrganizationId == version.OrganizationId
            && x.IsActive && x.DeletedAt == null, ct);
        var organizationActive = await db.Organizations.AsNoTracking().AnyAsync(x =>
            x.Id == version.OrganizationId && x.IsActive && x.DeletedAt == null, ct);
        if (!revisionReady || !buildingActive || !organizationActive)
            return AuthResult<ReleaseResponse>.Fail("INVALID_STATE", "Release requires an active organization, building and confirmed revision.", 409);

        var reviewValid = await db.RevisionReviews.AsNoTracking().AnyAsync(x =>
            x.Id == request.ConfirmationReviewId && x.RevisionId == request.RevisionId
            && x.ScenarioVersionId == request.ScenarioVersionId && x.Action == ReviewAction.ConfirmForTraining, ct);
        if (!reviewValid)
            return AuthResult<ReleaseResponse>.Fail("INVALID_STATE", "A matching ConfirmForTraining review is required.", 409);

        var artifactValid = await db.RevisionArtifacts.AsNoTracking().AnyAsync(x =>
            x.Id == request.CandidateArtifactId && x.RevisionId == request.RevisionId, ct);
        if (!artifactValid)
            return AuthResult<ReleaseResponse>.Fail("INVALID_STATE", "Candidate artifact does not belong to the revision.", 409);

        if (await db.Releases.AnyAsync(x =>
                x.RevisionId == request.RevisionId && x.ScenarioVersionId == request.ScenarioVersionId, ct))
            return AuthResult<ReleaseResponse>.Fail("RELEASE_EXISTS", "A release already exists for this revision and scenario version.", 409);

        var now = clock.GetUtcNow().UtcDateTime;
        var release = new Release
        {
            Id = Guid.NewGuid(), RevisionId = request.RevisionId, ScenarioVersionId = request.ScenarioVersionId,
            BuildingId = version.BuildingId, OrganizationId = version.OrganizationId,
            ConfirmationReviewId = request.ConfirmationReviewId, Status = ReleaseStatus.Built,
            SafetyThresholds = request.SafetyThresholds, CreatedAt = now, UpdatedAt = now
        };
        var package = new ReleasePackage
        {
            Id = Guid.NewGuid(), ReleaseId = release.Id, CandidateArtifactId = request.CandidateArtifactId,
            ManifestUrl = request.ManifestUrl.Trim(), ManifestSha256 = request.ManifestSha256.ToLowerInvariant(),
            PackageUrl = request.PackageUrl.Trim(), ChecksumSha256 = request.ChecksumSha256.ToLowerInvariant(),
            PackageSizeBytes = request.PackageSizeBytes, MinRuntimeVersion = request.MinRuntimeVersion.Trim(),
            SchemaVersion = request.SchemaVersion.Trim(), BuildTarget = request.BuildTarget.Trim(), CreatedAt = now
        };

        db.Releases.Add(release);
        db.ReleasePackages.Add(package);
        db.AuditLogs.Add(Audit(actorId, version.OrganizationId, release.Id, AuditAction.Create,
            null, $$"""{"status":"Built","revisionId":"{{release.RevisionId}}","scenarioVersionId":"{{release.ScenarioVersionId}}"}""", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AuthResult<ReleaseResponse>.Ok(ToResponse(release, package));
    }

    public async Task<ReleaseResponse?> GetAsync(Guid releaseId, Guid? organizationId, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().Include(x => x.ReleasePackage)
            .SingleOrDefaultAsync(x => x.Id == releaseId
                && (!organizationId.HasValue || x.OrganizationId == organizationId.Value), ct);
        return release?.ReleasePackage is null ? null : ToResponse(release, release.ReleasePackage);
    }

    public async Task<AuthResult<bool>> PublishAsync(
        Guid actorId, Guid releaseId, Guid? organizationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var release = await db.Releases.Include(x => x.ReleasePackage).SingleOrDefaultAsync(x => x.Id == releaseId, ct);
        if (release is null || (organizationId.HasValue && release.OrganizationId != organizationId.Value))
            return AuthResult<bool>.Fail("NOT_FOUND", "Release not found or access denied.", 404);
        if (release.Status != ReleaseStatus.Built)
            return AuthResult<bool>.Fail("INVALID_STATE", "Release must be Built before publishing.", 409);
        if (release.ReleasePackage is null)
            return AuthResult<bool>.Fail("INVALID_STATE", "Release package is required before publishing.", 409);

        var now = clock.GetUtcNow().UtcDateTime;
        release.Status = ReleaseStatus.Published;
        release.PublishedAt = now;
        release.PublishedBy = actorId;
        release.UpdatedAt = now;
        db.AuditLogs.Add(Audit(actorId, release.OrganizationId, releaseId, AuditAction.Publish,
            "{\"status\":\"Built\"}", "{\"status\":\"Published\"}", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    public async Task<AuthResult<bool>> RevokeAsync(
        Guid actorId, Guid releaseId, Guid? organizationId, string reason, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == releaseId, ct);
        if (release is null || (organizationId.HasValue && release.OrganizationId != organizationId.Value))
            return AuthResult<bool>.Fail("NOT_FOUND", "Release not found or access denied.", 404);
        if (release.Status == ReleaseStatus.Revoked) return AuthResult<bool>.Ok(true);
        if (release.Status is not (ReleaseStatus.Built or ReleaseStatus.Published))
            return AuthResult<bool>.Fail("INVALID_STATE", "Only Built or Published releases can be revoked.", 409);

        var previous = release.Status.ToString();
        var now = clock.GetUtcNow().UtcDateTime;
        release.Status = ReleaseStatus.Revoked;
        release.RevokedAt = now;
        release.RevokedBy = actorId;
        release.RevokedReason = reason;
        release.UpdatedAt = now;
        db.AuditLogs.Add(Audit(actorId, release.OrganizationId, releaseId, AuditAction.Revoke,
            $$"""{"status":"{{previous}}"}""", $$"""{"status":"Revoked","reason":{{System.Text.Json.JsonSerializer.Serialize(reason)}}}""", now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    private static AuditLog Audit(Guid actorId, Guid organizationId, Guid releaseId, AuditAction action,
        string? oldValues, string newValues, DateTime now) => new()
        {
            Id = Guid.NewGuid(), UserId = actorId, OrganizationId = organizationId, ActorType = "User",
            Action = action, TargetEntity = "Release", TargetId = releaseId, CorrelationId = Guid.NewGuid(),
            OldValues = oldValues, NewValues = newValues, CreatedAt = now
        };

    private static ReleaseResponse ToResponse(Release release, ReleasePackage package) => new(
        release.Id, release.RevisionId, release.ScenarioVersionId, release.BuildingId, release.OrganizationId,
        release.ConfirmationReviewId, release.Status.ToString(), release.SafetyThresholds,
        release.PublishedBy, release.PublishedAt, release.RevokedBy, release.RevokedReason, release.RevokedAt,
        release.CreatedAt, release.UpdatedAt,
        new(package.Id, package.CandidateArtifactId, package.ManifestUrl, package.ManifestSha256,
            package.PackageUrl, package.ChecksumSha256, package.PackageSizeBytes, package.MinRuntimeVersion,
            package.SchemaVersion, package.BuildTarget));
}
