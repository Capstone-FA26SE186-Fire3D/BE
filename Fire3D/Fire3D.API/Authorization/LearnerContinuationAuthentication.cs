using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Fire3D.Application.Learning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Fire3D.API.Authorization;

/// <summary>
/// Validates learner continuation tokens: separate key, issuer and audience, purpose learner_continuation. The token only names
/// its owner and one started session; the database gate checks it is the current continuation of that session.
/// </summary>
public static class LearnerContinuationAuthentication
{
    public const string SchemeName = "LearnerContinuation";

    public static IServiceCollection AddLearnerContinuation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LearnerSessionOptions>().Bind(configuration.GetSection("LearnerSessions"))
            .Validate(o => !o.Enabled || (Encoding.UTF8.GetByteCount(o.SigningKey) is >= 32 and <= 512 && Encoding.UTF8.GetByteCount(o.ContinuationSigningKey) is >= 32 and <= 512
                && o.SigningKey != o.ContinuationSigningKey && o.SigningKey != configuration["Jwt:SigningKey"] && o.ContinuationSigningKey != configuration["Jwt:SigningKey"]
                && o.Audience != o.ContinuationAudience && o.ContinuationAudience != configuration["Jwt:Audience"]),
                "Enabled learner sessions require separate launch and continuation keys (32-512 bytes) and distinct audiences.")
            .ValidateOnStart();
        services.AddAuthentication().AddJwtBearer(SchemeName);
        services.AddOptions<JwtBearerOptions>(SchemeName).Configure<IOptions<LearnerSessionOptions>>((bearer, configured) =>
        {
            var o = configured.Value;
            bearer.MapInboundClaims = false;bearer.SaveToken = false;bearer.IncludeErrorDetails = false;
            // An unset key keeps the scheme registered but unable to validate any token.
            var key = Encoding.UTF8.GetByteCount(o.ContinuationSigningKey) >= 32 ? o.ContinuationSigningKey : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            bearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = o.ContinuationIssuer, ValidateAudience = true, ValidAudience = o.ContinuationAudience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = JwtRegisteredClaimNames.Sub, RoleClaimType = "role"
            };
            bearer.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var p = context.Principal!;
                    if (p.FindFirst("purpose")?.Value != "learner_continuation" || !Guid.TryParse(p.FindFirst("session_id")?.Value, out _)
                        || !Guid.TryParse(p.FindFirst("jti")?.Value, out _) || !Guid.TryParse(p.FindFirst("sub")?.Value, out _)) context.Fail("Invalid continuation.");
                    return Task.CompletedTask;
                }
            };
        });
        return services;
    }
}
