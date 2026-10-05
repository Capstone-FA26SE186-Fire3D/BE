using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication;

public sealed record GoogleOnboardingProof(string Token, DateTime ExpiresAt);
public sealed record GoogleOnboardingCompleteRequest(string OnboardingToken, UserRole AccountType,
    string? Username = null, string? FullName = null, DateOnly? Dob = null, UserGender? Gender = null,
    string? PhoneNumber = null, string? OrganizationName = null, string? OrganizationAddress = null,
    string? OrganizationPhoneNumber = null);
public sealed record GoogleOnboardingCompletion(AccountResponse User, bool Replayed);

public interface IGoogleOnboardingService
{
    Task<AuthResult<GoogleOnboardingProof>> BeginAsync(VerifiedIdentity identity, CancellationToken ct);
    Task<AuthResult<GoogleOnboardingCompletion>> CompleteAsync(GoogleOnboardingCompleteRequest request, CancellationToken ct);
}
