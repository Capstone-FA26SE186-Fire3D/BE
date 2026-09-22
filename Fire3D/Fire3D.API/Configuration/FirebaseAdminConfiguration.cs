namespace Fire3D.API.Configuration;

public static class FirebaseAdminConfiguration
{
    private const string CredentialFileName = "firebase-admin.json";

    public static string? GetCredentialJson(IConfiguration configuration, string contentRootPath)
    {
        var configuredCredential = configuration["Firebase:ServiceAccountJson"];
        if (!string.IsNullOrWhiteSpace(configuredCredential))
        {
            return configuredCredential;
        }

        var section = configuration.GetSection("FirebaseAdmin").Get<Dictionary<string, string>>();
        if (section is { Count: > 0 })
        {
            if (section.TryGetValue("private_key", out var key) && key is not null)
                section["private_key"] = key.Replace("\\n", "\n");
            return System.Text.Json.JsonSerializer.Serialize(section);
        }

        var credentialPath = Path.Combine(contentRootPath, CredentialFileName);
        return File.Exists(credentialPath) ? File.ReadAllText(credentialPath) : null;
    }
}
