using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Fire3D.Application.Authentication.Abstractions;
using FirebaseAdmin.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Fire3D.Application.Authentication;
using Newtonsoft.Json.Linq;

namespace Fire3D.Infrastructure.Authentication;

public class FirebaseIdentityProvider(
    HttpClient httpClient,
    IOptions<AuthEmailOptions> options,
    ILogger<FirebaseIdentityProvider> logger,
    IFirebaseGoogleTokenVerifier googleVerifier,
    TimeProvider clock) : IIdentityProvider
{
    private readonly string _apiKey = options.Value.FirebaseApiKey;

    public async Task<IdentityUser> CreateEmailPasswordUserAsync(string email, string password, string fullName, CancellationToken ct)
    {
        try
        {
            var args = new UserRecordArgs
            {
                Email = email,
                Password = password,
                DisplayName = fullName
            };
            var userRecord = await FirebaseAuth.DefaultInstance.CreateUserAsync(args, ct);
            return new IdentityUser(userRecord.Uid, userRecord.Email, userRecord.DisplayName);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
        {
            throw new Exception("EmailAlreadyExists");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CreateEmailPasswordUserAsync failed");
            throw new Exception("ProviderUnavailable");
        }
    }

    public async Task<VerifiedIdentity> SignInWithPasswordAsync(string email, string password, CancellationToken ct)
    {
        var request = new { email, password, returnSecureToken = true };
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={_apiKey}";
        var response = await httpClient.PostAsJsonAsync(url, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Firebase SignInWithPassword failed: {Error}", error);
            
            if (error.Contains("INVALID_LOGIN_CREDENTIALS") || error.Contains("INVALID_PASSWORD") || error.Contains("EMAIL_NOT_FOUND"))
                throw new Exception("InvalidCredentials");
                
            if (error.Contains("USER_DISABLED"))
                throw new Exception("IdentityDisabled");

            throw new Exception("ProviderUnavailable");
        }

        var data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        var uid = data?["localId"]?.GetValue<string>();
        var userEmail = data?["email"]?.GetValue<string>();

        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(userEmail))
        {
            throw new Exception("InvalidCredentials");
        }

        return new VerifiedIdentity(uid, userEmail);
    }

    public async Task<VerifiedIdentity> VerifyGoogleTokenAsync(string idToken, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var decoded = await googleVerifier.VerifyAsync(idToken, true, linked.Token).WaitAsync(linked.Token);
            var claims = decoded.Claims;
            var email = claims.TryGetValue("email", out var emailValue) && emailValue is string text
                ? PasswordResetValidation.NormalizeEmail(text) : null;
            if (string.IsNullOrWhiteSpace(decoded.Uid) || decoded.Uid.Length > 128 || email is null
                || !claims.TryGetValue("email_verified", out var verified) || verified is not true
                || !claims.TryGetValue("firebase", out var firebase) || SignInProvider(firebase) != "google.com")
                throw new GoogleIdentityException(GoogleIdentityFailure.InvalidToken);
            return new VerifiedIdentity(decoded.Uid, email);
        }
        catch (GoogleIdentityException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode is AuthErrorCode.InvalidIdToken
            or AuthErrorCode.ExpiredIdToken or AuthErrorCode.RevokedIdToken or AuthErrorCode.UserNotFound or AuthErrorCode.TenantIdMismatch)
        {
            throw new GoogleIdentityException(GoogleIdentityFailure.InvalidToken);
        }
        catch (ArgumentException) { throw new GoogleIdentityException(GoogleIdentityFailure.InvalidToken); }
        catch (Exception ex)
        {
            // Do not attach SDK exception messages/stack traces or claims to provider logs.
            logger.LogWarning("Google verification unavailable. FailureKind={FailureKind}", ex.GetType().Name);
            throw new GoogleIdentityException(GoogleIdentityFailure.ProviderUnavailable);
        }
    }

    private static string? SignInProvider(object firebase) => firebase switch
    {
        IReadOnlyDictionary<string, object> values when values.TryGetValue("sign_in_provider", out var provider) => provider as string,
        IDictionary<string, object> values when values.TryGetValue("sign_in_provider", out var provider) => provider as string,
        JObject values when values["sign_in_provider"]?.Type == JTokenType.String => values["sign_in_provider"]!.Value<string>(),
        _ => null
    };

    public async Task DeleteUserAsync(string uid, CancellationToken ct)
    {
        try
        {
            await FirebaseAuth.DefaultInstance.DeleteUserAsync(uid, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteUserAsync failed for uid {Uid}", uid);
            throw new Exception("ProviderUnavailable");
        }
    }
}

