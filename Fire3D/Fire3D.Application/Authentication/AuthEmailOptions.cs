namespace Fire3D.Application.Authentication;

public class AuthEmailOptions
{
    public const string SectionName = "AuthEmail";
    public string FrontendUrl { get; set; } = string.Empty;
    public string FirebaseApiKey { get; set; } = string.Empty;
}
