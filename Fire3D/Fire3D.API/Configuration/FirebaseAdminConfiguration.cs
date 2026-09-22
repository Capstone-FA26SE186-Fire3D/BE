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

        var credentialPath = Path.Combine(contentRootPath, CredentialFileName);
        return File.Exists(credentialPath) ? File.ReadAllText(credentialPath) : null;
    }
}
