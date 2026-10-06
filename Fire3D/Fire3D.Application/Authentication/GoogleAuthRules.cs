using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication;

// Shared application rules for infrastructure-backed Google account operations.
public static class GoogleAuthRules
{
    public static Task<TokenResponse> IssueSessionAsync(IAuthStore store, ITokenService tokens,
        User user, Guid familyId, DateTime now, CancellationToken ct) =>
        AuthSupport.IssueAsync(store, tokens, user, familyId, now, now.Add(tokens.RefreshTokenLifetime), ct);
    public static DateTime UtcNow(TimeProvider clock) => AuthSupport.UtcNow(clock);
    public static AccountResponse ToAccount(User user) => AuthSupport.ToAccount(user);
    public static string? NormalizeDisplayName(string? name) => SelfRegistrationValidation.NormalizeName(name);
    public static Task<bool> IsActiveAsync(IAuthStore store, User user, CancellationToken ct) => AuthSupport.IsActiveAsync(store, user, ct);
    public static AuthResult<T> Conflict<T>(RegisterConflict conflict) => SelfRegistrationValidation.Conflict<T>(conflict);

    public static AuthResult<GoogleOnboardingInput> Validate(GoogleOnboardingCompleteRequest request, DateOnly today)
    {
        var username = SelfRegistrationValidation.NormalizeUsername(request.Username);
        var name = SelfRegistrationValidation.NormalizeName(request.FullName);
        var phone = request.PhoneNumber is null ? null : SelfRegistrationValidation.NormalizePhone(request.PhoneNumber);
        var orgName = SelfRegistrationValidation.NormalizeRequired(request.OrganizationName, 255);
        var address = SelfRegistrationValidation.NormalizeRequired(request.OrganizationAddress, 2000);
        var orgPhone = SelfRegistrationValidation.NormalizePhone(request.OrganizationPhoneNumber);
        var errors = new Dictionary<string, string[]>();
        void Add(string field, bool invalid, string message) { if (invalid) errors[field] = [message]; }
        Add("accountType", request.AccountType is not (UserRole.Trainee or UserRole.OrganizationUser), "Choose Trainee or OrganizationUser.");
        Add("username", request.AccountType == UserRole.Trainee && username is null, "Username must match [a-z0-9._-]{3,30}.");
        Add("username", request.AccountType == UserRole.OrganizationUser && request.Username is not null, "OrganizationUser can set username later through profile.");
        Add("fullName", request.FullName is not null && name is null, "Full name must not be empty and is limited to 200 characters.");
        Add("dob", request.Dob > today, "Date of birth must not be in the future.");
        Add("gender", request.Gender.HasValue && !Enum.IsDefined(request.Gender.Value), "Invalid gender.");
        Add("phoneNumber", request.PhoneNumber is not null && phone is null, "Use 6-15 digits, optionally prefixed by +.");
        if (request.AccountType == UserRole.OrganizationUser)
        {
            Add("organizationName", orgName is null, "Organization name is required and limited to 255 characters.");
            Add("organizationAddress", address is null, "Organization address is required and limited to 2000 characters.");
            Add("organizationPhoneNumber", orgPhone is null, "Organization phone requires 6-15 digits.");
        }
        else Add("organizationName", request.OrganizationName is not null || request.OrganizationAddress is not null || request.OrganizationPhoneNumber is not null,
            "Organization fields are only accepted for OrganizationUser.");
        return errors.Count == 0
            ? AuthResult<GoogleOnboardingInput>.Ok(new(request.AccountType, username, name, request.Dob, request.Gender, phone, orgName, address, orgPhone))
            : SelfRegistrationValidation.Invalid<GoogleOnboardingInput>(errors);
    }
}

public sealed record GoogleOnboardingInput(UserRole AccountType, string? Username, string? FullName, DateOnly? Dob,
    UserGender? Gender, string? PhoneNumber, string? OrganizationName, string? OrganizationAddress, string? OrganizationPhoneNumber);
