using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication;
using FirebaseAdmin.Auth;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Authentication;

public sealed record ResetFirebaseUser(string Uid, bool HasPassword, bool Disabled);
public interface IFirebaseResetAdmin
{
    Task<ResetFirebaseUser> FindAsync(string email, CancellationToken ct);
    Task<string> GenerateLinkAsync(string email, CancellationToken ct);
}
public sealed class FirebaseResetAdmin : IFirebaseResetAdmin
{
    private static FirebaseAuth Auth => FirebaseAuth.DefaultInstance ?? throw PasswordResetException.Unavailable();
    public async Task<ResetFirebaseUser> FindAsync(string email, CancellationToken ct)
    {
        try
        {
            var user = await Auth.GetUserByEmailAsync(email,ct);
            return new(user.Uid,user.ProviderData.Any(p=>p.ProviderId=="password"),user.Disabled);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode==AuthErrorCode.UserNotFound)
        { throw new PasswordResetException("RESET_NOT_AVAILABLE","Password reset is unavailable for this account.",400,true); }
        catch (FirebaseAuthException) { throw PasswordResetException.Unavailable(); }
    }
    public async Task<string> GenerateLinkAsync(string email, CancellationToken ct)
    {
        try { return await Auth.GeneratePasswordResetLinkAsync(email,null,ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode==AuthErrorCode.UserNotFound)
        { throw new PasswordResetException("RESET_NOT_AVAILABLE","Password reset is unavailable for this account.",400,true); }
        catch (FirebaseAuthException) { throw PasswordResetException.Unavailable(); }
    }
}

public sealed class FirebasePasswordResetProvider(HttpClient http, IAuthStore accounts,
    IOptions<AuthEmailOptions> configured, IFirebaseResetAdmin admin) : IPasswordResetProvider
{
    private readonly AuthEmailOptions options = configured.Value;
    private void RequireConfiguration()
    {
        if (!options.IsConfigured) throw PasswordResetException.Unavailable();
    }
    public async Task<string> GenerateResetLinkAsync(string email, CancellationToken ct)
    {
        RequireConfiguration();
        var user = await admin.FindAsync(email,ct);
        if (!user.HasPassword || user.Disabled)
            throw new PasswordResetException("RESET_NOT_AVAILABLE","Password reset is unavailable for this account.",400,true);
        var firebaseLink = new Uri(await admin.GenerateLinkAsync(email,ct));
        var code = System.Web.HttpUtility.ParseQueryString(firebaseLink.Query)["oobCode"];
        if (string.IsNullOrEmpty(code)) throw PasswordResetException.Unavailable();
        // Send users to our handler, not Firebase's hosted page which would bypass Fire3D session revocation.
        return options.FrontendUrl.TrimEnd('/')+"/reset-password?mode=resetPassword&oobCode="+Uri.EscapeDataString(code);
    }
    public async Task<VerifiedResetIdentity> VerifyResetCodeAsync(string oobCode, CancellationToken ct)
    {
        using var data = await SendAsync(new { oobCode },ct);
        var email = data.RootElement.TryGetProperty("email",out var value)
            ? PasswordResetValidation.NormalizeEmail(value.GetString()) : null;
        if (email is null) throw PasswordResetException.InvalidCode();
        var firebase = await admin.FindAsync(email,ct);
        var user = await accounts.FindUserByEmailAsync(email,ct);
        if (!firebase.HasPassword || firebase.Disabled || user is null || user.FirebaseUid != firebase.Uid)
            throw PasswordResetException.InvalidCode();
        return new(user.Id,firebase.Uid);
    }
    public async Task ConfirmResetAsync(string oobCode, string newPassword, CancellationToken ct)
    {
        using var result = await SendAsync(new { oobCode,newPassword },ct);
    }
    private async Task<JsonDocument> SendAsync(object body, CancellationToken ct)
    {
        RequireConfiguration();
        try
        {
            using var response = await http.PostAsJsonAsync(
                "https://identitytoolkit.googleapis.com/v1/accounts:resetPassword?key="+Uri.EscapeDataString(options.FirebaseApiKey),body,ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var document = await JsonDocument.ParseAsync(stream,cancellationToken:ct);
            if (response.IsSuccessStatusCode) return document;
            using (document)
            {
                var code = document.RootElement.TryGetProperty("error",out var error) && error.TryGetProperty("message",out var message)
                    ? message.GetString()?.Split(' ')[0] : null;
                if ((int)response.StatusCode==400)
                {
                    if (code is "INVALID_OOB_CODE" or "EXPIRED_OOB_CODE" or "EMAIL_NOT_FOUND" or "USER_DISABLED")
                        throw PasswordResetException.InvalidCode();
                    if (code is "WEAK_PASSWORD" or "PASSWORD_DOES_NOT_MEET_REQUIREMENTS")
                        throw new PasswordResetException("INVALID_PASSWORD","Password does not meet the identity provider policy.",400,true);
                }
                throw PasswordResetException.Unavailable();
            }
        }
        catch (HttpRequestException) { throw PasswordResetException.Unavailable(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PasswordResetException.Unavailable(); }
        catch (JsonException) { throw PasswordResetException.Unavailable(); }
    }
}

