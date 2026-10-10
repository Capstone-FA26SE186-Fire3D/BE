using System.Reflection;
namespace Fire3D.API.Extensions;
public sealed record ArtifactVersionResponse(string Version);
public static class ArtifactVersion
{
    public static string Get(IConfiguration configuration)=>configuration["APP_VERSION"]
        ?? typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
