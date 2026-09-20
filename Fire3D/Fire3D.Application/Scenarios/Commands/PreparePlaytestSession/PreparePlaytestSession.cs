using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;

public sealed record PreparePlaytestRequest(
    Guid RevisionId,
    Guid? ScenarioDraftId,
    Guid? ScenarioVersionId,
    string PackageHash,
    string ProtocolVersion,
    string ManifestSchemaVersion,
    string? RuntimeVersion
);

public sealed record PreparePlaytestSessionCommand(Guid ActorId, Guid BuildingId, PreparePlaytestRequest Request) : IRequest<AuthResult<Guid>>;

public interface IPlaytestWriteStore
{
    Task<AuthResult<Guid>> PreparePlaytestAsync(Guid actorId, Guid buildingId, Guid organizationId, PreparePlaytestRequest request, CancellationToken ct);
}

public sealed class PreparePlaytestSessionHandler(IAuthStore accounts, IPlaytestWriteStore store)
    : IRequestHandler<PreparePlaytestSessionCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(PreparePlaytestSessionCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.Request.ScenarioDraftId == null && command.Request.ScenarioVersionId == null)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Either ScenarioDraftId or ScenarioVersionId must be provided.", 400);

        return await store.PreparePlaytestAsync(command.ActorId, command.BuildingId, scope.Value!.OrganizationId ?? Guid.Empty, command.Request, ct);
    }
}
