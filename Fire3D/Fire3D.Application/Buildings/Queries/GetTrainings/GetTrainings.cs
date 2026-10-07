using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Buildings.Queries.GetTrainings;

public sealed record GetTrainingsQuery(Guid ActorId, Guid BuildingId, Guid? Family = null) : IRequest<AuthResult<List<TrainingDto>>>;

public sealed record TrainingDto(
    Guid Id,
    Guid ReleaseId,
    string Name,
    string? Description,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] Fire3D.Domain.Enums.TrainingStatus Status,
    DateTime? StartDate,
    DateTime? EndDate,
    List<string> AllowedModes,
    DateTime CreatedAt
);

public interface ITrainingReadStore
{
    Task<AuthResult<List<TrainingDto>>> GetTrainingsByBuildingAsync(Guid actorId, Guid buildingId, Guid? organizationId, CancellationToken ct, Guid? family=null);
}

public sealed class GetTrainingsHandler(IAuthStore accounts, ITrainingReadStore store)
    : IRequestHandler<GetTrainingsQuery, AuthResult<List<TrainingDto>>>
{
    public async Task<AuthResult<List<TrainingDto>>> Handle(GetTrainingsQuery request, CancellationToken ct)
    {
        var actor=await accounts.FindUserAsync(request.ActorId,ct);
        if(actor is null || !actor.IsActive || actor.DeletedAt.HasValue)return AuthResult<List<TrainingDto>>.Fail("UNAUTHORIZED","Active account required.",401);
        if(actor.Role is not (Fire3D.Domain.Enums.UserRole.Trainee or Fire3D.Domain.Enums.UserRole.OrganizationUser or Fire3D.Domain.Enums.UserRole.PlatformAdmin))return AuthResult<List<TrainingDto>>.Fail("FORBIDDEN","Role cannot list Training.",403);
        return await store.GetTrainingsByBuildingAsync(request.ActorId,request.BuildingId,actor.Role==Fire3D.Domain.Enums.UserRole.OrganizationUser?actor.OrganizationId:null,ct,request.Family);
    }
}
