using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings.Queries.GetTrainings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Buildings;

public sealed class TrainingReadStore(Fire3DDbContext db) : ITrainingReadStore
{
    public async Task<AuthResult<List<TrainingDto>>> GetTrainingsByBuildingAsync(Guid actorId, Guid buildingId, Guid organizationId, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId, ct);
        if (building == null || (organizationId != Guid.Empty && building.OrganizationId != organizationId))
            return AuthResult<List<TrainingDto>>.Fail("NOT_FOUND", "Building not found or access denied.", 404);

        var trainings = await db.Trainings
            .Include(t => t.Release)
            .Where(t => t.Release.BuildingId == buildingId && t.Release.Status == Fire3D.Domain.Enums.ReleaseStatus.Published && t.Status == Fire3D.Domain.Enums.TrainingStatus.Active)
            .Select(t => new TrainingDto(
                t.Id,
                t.ReleaseId,
                t.Name,
                t.Description,
                t.Status,
                t.StartDate,
                t.EndDate,
                t.AllowedModes,
                t.CreatedAt
            ))
            .ToListAsync(ct);

        return AuthResult<List<TrainingDto>>.Ok(trainings);
    }
}
