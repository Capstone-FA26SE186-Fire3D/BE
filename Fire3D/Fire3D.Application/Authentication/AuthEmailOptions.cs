namespace Fire3D.Application.Authentication;

/// <summary>Options cho forgot-password flow, đọc từ appsettings "Auth" section.</summary>
public sealed class AuthEmailOptions
{
    public const string SectionName = "Auth";
    /// <summary>Base URL của frontend, vd: https://fire3d.vercel.app</summary>
    public string FrontendUrl { get; init; } = "http://localhost:3000";
}
