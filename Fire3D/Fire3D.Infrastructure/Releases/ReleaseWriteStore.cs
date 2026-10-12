using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Releases;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Releases;

public sealed class ReleaseWriteStore(Fire3DDbContext db, TimeProvider clock, IOptions<PublishingOptions>? publishing = null) : IReleaseStore
{
    private readonly TimeProvider legacyClock = clock;
    public async Task<AuthResult<ReleaseResponse>> BuildAsync(Guid actorId,Guid? organizationId,BuildReleaseRequest request,CancellationToken ct,string? key=null,Guid? family=null)
    {
        var result=await JsonCommandGate.Execute(db,"release_access_gate","Build",actorId,family,request.ScenarioVersionId,request,key,null,ct);
        return result.IsSuccess?AuthResult<ReleaseResponse>.Ok(result.Value.Deserialize<ReleaseResponse>(JsonCommandGate.Json)!):new(default,result.Error);
    }

    public async Task<ReleaseResponse?> GetAsync(Guid releaseId, Guid? organizationId, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().Include(x => x.ReleasePackage)
            .SingleOrDefaultAsync(x => x.Id == releaseId
                && (!organizationId.HasValue || x.OrganizationId == organizationId.Value), ct);
        if (release?.ReleasePackage is null) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        var entitlement = await db.Set<ServiceEntitlement>().AsNoTracking()
            .Where(x => x.BuildingId == release.BuildingId && (x.Status == "Active" || x.Status == "Trial") && x.StartsAt <= now && x.EndsAt > now)
            .OrderByDescending(x => x.PaymentTransactionId != null).ThenByDescending(x => x.EndsAt).ThenBy(x => x.Id)
            .Select(x => new ReleaseEntitlementSummary(x.Id, x.Status, x.StartsAt, x.EndsAt, x.PaymentTransactionId != null, x.LearnerLimit))
            .FirstOrDefaultAsync(ct);
        return ToResponse(release, release.ReleasePackage) with { Entitlement = entitlement };
    }

    public async Task<Fire3D.Application.Administration.PageResponse<ReleaseSummaryResponse>> ListAsync(Guid? organizationId, ReleaseFilter filter, CancellationToken ct)
    {
        var query = from r in db.Releases.AsNoTracking()
                    join v in db.ScenarioVersions.AsNoTracking() on r.ScenarioVersionId equals v.Id
                    where !organizationId.HasValue || r.OrganizationId == organizationId.Value
                    select new { r, v };
        if (filter.BuildingId.HasValue) query = query.Where(x => x.r.BuildingId == filter.BuildingId.Value);
        if (filter.ScenarioId.HasValue) query = query.Where(x => x.v.ScenarioId == filter.ScenarioId.Value);
        if (filter.Status is not null && Enum.TryParse<ReleaseStatus>(filter.Status, false, out var status)) query = query.Where(x => x.r.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.r.CreatedAt).ThenBy(x => x.r.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new { x.r.Id, x.r.BuildingId, x.v.ScenarioId, x.r.ScenarioVersionId, x.v.VersionNumber, x.v.Name, x.r.Status, x.r.CreatedAt, x.r.PublishedAt, x.r.RevokedAt })
            .ToListAsync(ct);
        return new(rows.Select(x => new ReleaseSummaryResponse(x.Id, x.BuildingId, x.ScenarioId, x.ScenarioVersionId, x.VersionNumber, x.Name,
            x.Status.ToString(), x.CreatedAt, x.PublishedAt, x.RevokedAt)).ToList(), total, filter.Page, filter.PageSize);
    }

    public async Task<AuthResult<ReleaseResponse>> PublishAsync(Guid actorId,Guid releaseId,Guid? organizationId,CancellationToken ct,Guid? family=null,string? key=null)
    {
        if(publishing?.Value.Enabled != true)
            return AuthResult<ReleaseResponse>.Fail("PUBLISH_GATE_UNAVAILABLE","Publishing is disabled until deployment gates are verified.",503);
        var result=await JsonCommandGate.Execute(db,"publish_release_gate","Publish",actorId,family,releaseId,new{},key??releaseId.ToString(),null,ct);
        return result.IsSuccess?AuthResult<ReleaseResponse>.Ok(result.Value.Deserialize<ReleaseResponse>(JsonCommandGate.Json)!):new(default,result.Error);
    }
    public async Task<AuthResult<bool>> RevokeAsync(Guid actorId,Guid releaseId,Guid? organizationId,string reason,CancellationToken ct,Guid? family=null)
    {
        var result=await JsonCommandGate.Execute(db,"release_access_gate","Revoke",actorId,family,releaseId,new{reason},null,null,ct);
        return result.IsSuccess?AuthResult<bool>.Ok(true):new(default,result.Error);
    }

    private static ReleaseResponse ToResponse(Release release, ReleasePackage package) => new(
        release.Id, release.RevisionId, release.ScenarioVersionId, release.BuildingId, release.OrganizationId,
        release.ConfirmationReviewId, release.Status.ToString(), release.SafetyThresholds,
        release.PublishedBy, release.PublishedAt, release.RevokedBy, release.RevokedReason, release.RevokedAt,
        release.CreatedAt, release.UpdatedAt,
        new(package.Id, package.CandidateArtifactId, package.ManifestUrl, package.ManifestSha256,
            package.PackageUrl, package.ChecksumSha256, package.PackageSizeBytes, package.MinRuntimeVersion,
            package.SchemaVersion, package.BuildTarget));
}
