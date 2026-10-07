using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Releases;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Releases;

public sealed class ReleaseWriteStore(Fire3DDbContext db, TimeProvider clock) : IReleaseStore
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
        return release?.ReleasePackage is null ? null : ToResponse(release, release.ReleasePackage);
    }

    public Task<AuthResult<bool>> PublishAsync(Guid actorId,Guid releaseId,Guid? organizationId,CancellationToken ct)=>Task.FromResult(AuthResult<bool>.Fail("PUBLISH_GATE_UNAVAILABLE","Publish requires the separately deployed entitlement/readiness gate.",503));
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
