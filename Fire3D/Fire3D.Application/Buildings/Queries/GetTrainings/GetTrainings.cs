using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Buildings.Queries.GetTrainings;

public sealed record GetTrainingsQuery(Guid ActorId, Guid BuildingId) : IRequest<AuthResult<List<TrainingDto>>>;

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
    Task<AuthResult<List<TrainingDto>>> GetTrainingsByBuildingAsync(Guid actorId, Guid buildingId, Guid organizationId, CancellationToken ct);
}

public sealed class GetTrainingsHandler(IAuthStore accounts, ITrainingReadStore store)
    : IRequestHandler<GetTrainingsQuery, AuthResult<List<TrainingDto>>>
{
    public async Task<AuthResult<List<TrainingDto>>> Handle(GetTrainingsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.GetTrainingsByBuildingAsync(request.ActorId, request.BuildingId, scope.Value!.OrganizationId ?? Guid.Empty, ct);
    }
}
