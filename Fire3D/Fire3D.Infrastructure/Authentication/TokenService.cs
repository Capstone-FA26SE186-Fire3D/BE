using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Fire3D.Infrastructure.Authentication;

public sealed class TokenService(IOptions<JwtOptions> configuration) : ITokenService
{
    private readonly JwtOptions options = configuration.Value;
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(options.RefreshTokenDays);
    public AccessTokenValue CreateAccessToken(User user, Guid familyId, DateTime now)
    {
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("role", user.Role.ToString()), new("sid", familyId.ToString())
        };
        if (user.OrganizationId is Guid organizationId)
            claims.Add(new("organization_id", organizationId.ToString()));
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, now, expires,
            new SigningCredentials(options.GetKey(), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
    public string CreateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
    public string HashRefreshToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
