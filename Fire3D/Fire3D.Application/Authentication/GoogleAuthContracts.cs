using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Domain.Enums;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Authentication;

public sealed record GoogleOnboardingProof(string Token, DateTime ExpiresAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoogleOnboardingCompleteRequest(string OnboardingToken,
    [property: JsonConverter(typeof(GoogleAccountTypeJsonConverter))] UserRole AccountType,
    string? Username = null, string? FullName = null, DateOnly? Dob = null, UserGender? Gender = null,
    string? PhoneNumber = null, string? OrganizationName = null, string? OrganizationAddress = null,
    string? OrganizationPhoneNumber = null);
public sealed record GoogleOnboardingCompletion(TokenResponse Authentication);

// Applies only to onboarding account selection, never to authorization role serialization.
public sealed class GoogleAccountTypeJsonConverter : JsonConverter<UserRole>
{
    public override bool HandleNull => true;
    public override UserRole Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() switch
            {
                "trainee" or "Trainee" => UserRole.Trainee,
                "organization" or "OrganizationUser" => UserRole.OrganizationUser,
                _ => (UserRole)(-1)
            };
        reader.Skip();
        return (UserRole)(-1); // Application returns VALIDATION_ERROR/errors.accountType.
    }
    public override void Write(Utf8JsonWriter writer, UserRole value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { UserRole.Trainee => "trainee", UserRole.OrganizationUser => "organization", _ => "invalid" });
}

public interface IGoogleOnboardingService
{
    Task<AuthResult<GoogleOnboardingProof>> BeginAsync(VerifiedIdentity identity, CancellationToken ct);
    Task<AuthResult<GoogleOnboardingCompletion>> CompleteAsync(GoogleOnboardingCompleteRequest request, CancellationToken ct);
}

public sealed record GoogleLinkRequest(string IdToken, string CurrentPassword);
public sealed record GoogleLinkResponse(AccountResponse User, bool AlreadyLinked, bool RequiresLogin);
public interface IGoogleLinkService
{
    Task<AuthResult<GoogleLinkResponse>> LinkAsync(Guid actorId, Guid sessionFamilyId, GoogleLinkRequest request, CancellationToken ct);
}
