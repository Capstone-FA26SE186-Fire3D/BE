using Fire3D.Application.Authentication;

namespace Fire3D.AuthTests;

internal static class GoogleOnboardingTestDoubles
{
    internal static IGoogleOnboardingService Proofs() => ResetProxy.For<IGoogleOnboardingService>((method, _) =>
        method == nameof(IGoogleOnboardingService.BeginAsync)
            ? Task.FromResult(AuthResult<GoogleOnboardingProof>.Ok(new(new string('a', 43), DateTime.UtcNow.AddMinutes(15))))
            : throw new InvalidOperationException(method));
}
