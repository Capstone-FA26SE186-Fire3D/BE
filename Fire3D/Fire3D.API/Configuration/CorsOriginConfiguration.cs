namespace Fire3D.API.Configuration;

public static class CorsOriginConfiguration
{
    private const string DefaultLocalOrigin = "http://localhost:5173";

    public static string[] GetAllowedOrigins(IConfiguration configuration)
    {
        var configuredOrigins = configuration.GetSection("Auth:FrontendUrls").Get<string[]>()
            ?.Select(Normalize)
            .Where(origin => origin is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (configuredOrigins is { Length: > 0 }) return configuredOrigins;

        return [Normalize(configuration["Auth:FrontendUrl"]) ?? DefaultLocalOrigin];
    }

    private static string? Normalize(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        return origin.Trim().TrimEnd('/');
    }
}
