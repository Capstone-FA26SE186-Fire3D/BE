namespace Fire3D.Application.Authentication;
public class AuthEmailOptions
{
    public const string SectionName = "AuthEmail";
    /// <summary>
    /// Public origin serving the FET3D frontend. It is used for password-reset links and
    /// as the backwards-compatible verification-link origin.
    /// </summary>
    public string FrontendUrl { get; set; } = "https://fet3d.io.vn";
    /// <summary>
    /// Public base URL that serves the API's <c>/verify-email/</c> page. When omitted,
    /// <see cref="FrontendUrl"/> is retained for backwards-compatible deployments.
    /// </summary>
    public string? VerificationUrl { get; set; }
    public string FirebaseApiKey { get; set; } = string.Empty;
    public bool WorkerEnabled { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FirebaseApiKey) &&
        IsValidBaseUrl(FrontendUrl, allowLoopbackHttp: true);

    /// <summary>
    /// Validates public link origins during application startup. HTTP loopback addresses are
    /// intentionally permitted only while developing locally.
    /// </summary>
    public bool IsValidForEnvironment(bool allowLoopbackHttp) =>
        IsValidBaseUrl(FrontendUrl, allowLoopbackHttp) &&
        (string.IsNullOrWhiteSpace(VerificationUrl) || IsValidBaseUrl(VerificationUrl, allowLoopbackHttp));

    public string GetFrontendPageBaseUrl() => GetBaseUrl(FrontendUrl, "FrontendUrl");

    public string GetVerificationPageBaseUrl() => GetBaseUrl(
        string.IsNullOrWhiteSpace(VerificationUrl) ? FrontendUrl : VerificationUrl,
        "VerificationUrl (or FrontendUrl)");

    private static string GetBaseUrl(string? value, string settingName)
    {
        if (!IsValidBaseUrl(value, allowLoopbackHttp: true) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Configure AuthEmail:{settingName} as an HTTPS public base URL without user info, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }

    private static bool IsValidBaseUrl(string? value, bool allowLoopbackHttp) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (allowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
