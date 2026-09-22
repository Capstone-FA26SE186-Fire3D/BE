using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using FirebaseAdmin.Auth;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Authentication;

public class FirebasePasswordResetProvider(
    HttpClient httpClient,
    IAuthStore authStore,
    AuthEmailOptions options,
    ILogger<FirebasePasswordResetProvider> logger) : IPasswordResetProvider
{
    public async Task<string> GenerateResetLinkAsync(string email, CancellationToken ct)
    {
        var actionCodeSettings = new ActionCodeSettings
        {
            Url = options.FrontendUrl + "/reset-password",
            HandleCodeInApp = true
        };
        // Use FirebaseAdmin to generate password reset link
        return await FirebaseAuth.DefaultInstance.GeneratePasswordResetLinkAsync(email, actionCodeSettings, ct);
    }

    public async Task<VerifiedResetIdentity> VerifyResetCodeAsync(string oobCode, CancellationToken ct)
    {
        var request = new { oobCode };
        var response = await httpClient.PostAsJsonAsync($"https://identitytoolkit.googleapis.com/v1/accounts:resetPassword?key={options.FirebaseApiKey}", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Firebase VerifyResetCode failed: {Response}", content);
            throw new Exception("INVALID_RESET_CODE");
        }

        var data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        var email = data?["email"]?.GetValue<string>();
        if (string.IsNullOrEmpty(email)) throw new Exception("INVALID_RESET_CODE");

        var dbUser = await authStore.FindUserByEmailAsync(email, ct);
        if (dbUser == null || string.IsNullOrEmpty(dbUser.FirebaseUid))
            throw new Exception("INVALID_RESET_CODE");

        return new VerifiedResetIdentity(dbUser.Id, dbUser.FirebaseUid);
    }

    public async Task ConfirmResetAsync(string oobCode, string newPassword, CancellationToken ct)
    {
        var request = new { oobCode, newPassword };
        var response = await httpClient.PostAsJsonAsync($"https://identitytoolkit.googleapis.com/v1/accounts:resetPassword?key={options.FirebaseApiKey}", request, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Firebase ConfirmReset failed: {Response}", content);
            throw new Exception("INVALID_RESET_CODE");
        }
    }
}

