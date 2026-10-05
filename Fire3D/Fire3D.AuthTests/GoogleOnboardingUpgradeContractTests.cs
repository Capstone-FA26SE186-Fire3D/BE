using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Commands.FirebaseLogin;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingUpgradeContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    [Theory]
    [InlineData("trainee", UserRole.Trainee)]
    [InlineData("Trainee", UserRole.Trainee)]
    [InlineData("organization", UserRole.OrganizationUser)]
    [InlineData("OrganizationUser", UserRole.OrganizationUser)]
    public void Account_type_aliases_normalize_to_the_same_application_input(string name, UserRole role)
    {
        var request = JsonSerializer.Deserialize<GoogleOnboardingCompleteRequest>(
            "{\"onboardingToken\":\"proof\",\"accountType\":\"" + name + "\"}", Json)!;
        Assert.Equal(role, request.AccountType);
    }

    [Theory]
    [InlineData("\"PlatformAdmin\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Forbidden_account_types_have_a_field_validation_error(string value)
    {
        var request = JsonSerializer.Deserialize<GoogleOnboardingCompleteRequest>(
            "{\"onboardingToken\":\"proof\",\"accountType\":" + value + "}", Json)!;
        Assert.Contains("accountType", GoogleAuthRules.Validate(request, new DateOnly(2026, 10, 6)).Error!.Errors!.Keys);
    }

    [Fact]
    public async Task Exchange_contains_nested_verified_identity_and_keeps_proof_aliases()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            nameof(IAuthStore.FindUserByFirebaseUidAsync) or nameof(IAuthStore.FindUserByEmailAsync) => Task.FromResult<User?>(null),
            _ => throw new InvalidOperationException("Exchange must not create account/session: " + method)
        });
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity("uid", "user@example.test")));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new InvalidOperationException("No session before complete"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System,
            GoogleOnboardingTestDoubles.Proofs()).Handle(new("id-token"), default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, Json));
        var root = json.RootElement;
        var proof = root.GetProperty("onboarding");
        Assert.Equal(root.GetProperty("onboardingToken").GetString(), proof.GetProperty("token").GetString());
        Assert.Equal(root.GetProperty("expiresAt").GetString(), proof.GetProperty("expiresAt").GetString());
        Assert.Equal("user@example.test", proof.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("authentication").ValueKind);
    }

    [Theory]
    [InlineData("  Verified Google Name  ", "Verified Google Name")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public async Task Display_name_is_read_only_from_verified_claims_and_normalized(string? name, string? expected)
    {
        var claims = new Dictionary<string, object>
        { ["email"] = "user@example.test", ["email_verified"] = true,
          ["firebase"] = new Dictionary<string, object> { ["sign_in_provider"] = "google.com" } };
        if (name is not null) claims["name"] = name;
        var verifier = ResetProxy.For<IFirebaseGoogleTokenVerifier>((_, _) => Task.FromResult(new VerifiedFirebaseClaims("uid", claims)));
        var provider = new FirebaseIdentityProvider(new HttpClient(), Options.Create(new AuthEmailOptions()),
            NullLogger<FirebaseIdentityProvider>.Instance, verifier, TimeProvider.System);
        var identity = await provider.VerifyGoogleTokenAsync("fake-token", default);
        var property = typeof(VerifiedIdentity).GetProperty("DisplayName");
        Assert.NotNull(property);
        Assert.Equal(expected, property.GetValue(identity));
    }

    [BillingPostgresFact]
    public async Task Additive_display_name_migration_preserves_existing_proof_and_accounts()
    {
        await using var db = await BillingDatabase.Create(false);
        await GoogleOnboardingPostgresTests.Prepare(db, includeDisplayName: false);
        await db.Sql("INSERT INTO auth_google_onboarding_sessions(firebase_uid,email,onboarding_token_hash,expires_at) VALUES('legacy','legacy@example.test',repeat('a',64),now()+interval '15 minutes')");
        await BackendDatabasePermissionsTests.Apply(db, "AddGoogleOnboardingDisplayName");
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE firebase_uid='legacy' AND display_name IS NULL AND onboarding_token_hash=repeat('a',64)"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(false, await db.Scalar("SELECT has_table_privilege('anon','auth_google_onboarding_sessions','SELECT')"));
    }
}
