namespace Fire3D.Application.Authentication;
public class AuthEmailOptions
{
    public const string SectionName = "AuthEmail";
    public string FrontendUrl { get; set; } = "http://localhost:3000";
    public string FirebaseApiKey { get; set; } = string.Empty;
    public bool WorkerEnabled { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FirebaseApiKey) &&
        Uri.TryCreate(FrontendUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
