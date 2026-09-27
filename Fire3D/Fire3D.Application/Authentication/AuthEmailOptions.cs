namespace Fire3D.Application.Authentication;
public class AuthEmailOptions
{
    public const string SectionName = "AuthEmail";
    public string FrontendUrl { get; set; } = "http://localhost:3000";
    /// <summary>
    /// Public base URL that serves the API's <c>/verify-email/</c> page. When omitted,
    /// <see cref="FrontendUrl"/> is retained for backwards-compatible deployments.
    /// </summary>
    public string? VerificationUrl { get; set; }
    public string FirebaseApiKey { get; set; } = string.Empty;
    public bool WorkerEnabled { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FirebaseApiKey) &&
        Uri.TryCreate(FrontendUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public string GetVerificationPageBaseUrl()
    {
        var value = string.IsNullOrWhiteSpace(VerificationUrl) ? FrontendUrl : VerificationUrl;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Configure AuthEmail:VerificationUrl (or AuthEmail:FrontendUrl) as an HTTPS public base URL without user info, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
}
