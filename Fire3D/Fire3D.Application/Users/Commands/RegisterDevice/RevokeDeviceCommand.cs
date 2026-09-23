using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Users.Commands.RegisterDevice;

public sealed record RevokeDeviceCommand(Guid UserId, string DeviceUuid) : IRequest<AuthResult<bool>>;

public sealed class RevokeDeviceCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<RevokeDeviceCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(RevokeDeviceCommand request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty || !Guid.TryParse(request.DeviceUuid, out _))
            return AuthResult<bool>.Fail("VALIDATION_ERROR", "A valid device UUID is required.", 400);

        await store.RevokeDeviceAsync(request.UserId, request.DeviceUuid, clock.GetUtcNow().UtcDateTime, ct);
        return AuthResult<bool>.Ok(true);
    }
}
