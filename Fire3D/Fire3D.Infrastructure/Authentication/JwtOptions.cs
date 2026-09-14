using Microsoft.IdentityModel.Tokens;

namespace Fire3D.Infrastructure.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience)
            || AccessTokenMinutes is < 1 or > 60 || RefreshTokenDays is < 1 or > 30) return false;
        try { return Convert.FromBase64String(SigningKey).Length >= 32; }
        catch (FormatException) { return false; }
    }
    public SymmetricSecurityKey GetKey() => new(Convert.FromBase64String(SigningKey));
}
