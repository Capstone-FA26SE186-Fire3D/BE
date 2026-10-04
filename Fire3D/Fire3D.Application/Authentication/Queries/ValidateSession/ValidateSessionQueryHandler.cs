using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Queries.ValidateSession;

internal sealed class ValidateSessionQueryHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<ValidateSessionQuery, bool>
{
    public Task<bool> Handle(ValidateSessionQuery command,CancellationToken ct) =>
        store.SessionIsValidAsync(command.UserId,command.FamilyId,command.Role,command.OrganizationId,AuthSupport.UtcNow(clock),ct);
}
