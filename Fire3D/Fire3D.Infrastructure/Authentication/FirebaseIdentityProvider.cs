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

namespace Fire3D.Infrastructure.Authentication;

public class FirebaseIdentityProvider(
    HttpClient httpClient,
    IOptions<AuthEmailOptions> options,
    ILogger<FirebaseIdentityProvider> logger) : IIdentityProvider
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
        try
        {
            var decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken, true, ct);
            var uid = decodedToken.Uid;
            if (!decodedToken.Claims.TryGetValue("email_verified", out var verified) || verified is not true)
                throw new InvalidOperationException("Verified Google email is required.");
            using var claims = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(decodedToken.Claims));
            if (!claims.RootElement.TryGetProperty("firebase", out var firebase) ||
                !firebase.TryGetProperty("sign_in_provider", out var provider) || provider.GetString() != "google.com")
                throw new InvalidOperationException("Google sign-in is required.");
            var email = decodedToken.Claims.TryGetValue("email", out var emailObj) ? emailObj?.ToString() : null;

            if (string.IsNullOrEmpty(email))
            {
                throw new Exception("InvalidToken");
            }

            return new VerifiedIdentity(uid, email);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.RevokedIdToken || ex.AuthErrorCode == AuthErrorCode.ExpiredIdToken)
        {
            throw new Exception("InvalidToken");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VerifyGoogleTokenAsync failed");
            throw new Exception("InvalidToken");
        }
    }

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

