using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.CreateScenario;

public sealed record CreateScenarioRequest(Guid BuildingId, string Name);

public sealed record CreateScenarioCommand(Guid ActorId, CreateScenarioRequest Request) : IRequest<AuthResult<Guid>>;

public sealed class CreateScenarioHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<CreateScenarioCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(CreateScenarioCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.Request == null || command.Request.BuildingId == Guid.Empty || string.IsNullOrWhiteSpace(command.Request.Name))
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "BuildingId and Name are required.", 400);

        return await store.CreateScenarioAsync(command.ActorId, command.Request.BuildingId, scope.Value!.OrganizationId ?? Guid.Empty, command.Request, ct);
    }
}
