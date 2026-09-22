using System.IdentityModel.Tokens.Jwt;
using Fire3D.API.Authorization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Queries.ValidateSession;
using MediatR;
using Fire3D.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Fire3D.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddAccountAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(x => x.IsValid(), "Configure Jwt issuer, audience, a base64 signing key of at least 32 random bytes, and valid token lifetimes.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<ITokenService, TokenService>();
                services.AddScoped<IAuthStore, AuthStore>();
        services.AddOptions<AuthEmailOptions>().Bind(configuration.GetSection("AuthEmail")).ValidateDataAnnotations();
        services.AddSingleton<IFirebaseResetAdmin, FirebaseResetAdmin>();
        services.AddScoped<IPasswordResetStore, PasswordResetStore>();
        services.AddScoped<IPasswordResetQueue, PasswordResetQueue>();
        services.AddExceptionHandler<PasswordResetExceptionHandler>();
        services.AddHttpClient<IPasswordResetProvider, FirebasePasswordResetProvider>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers();
        services.AddHostedService<Fire3D.Infrastructure.Workers.PasswordResetWorker>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, configured) =>
            {
                var jwt = configured.Value;
                bearer.MapInboundClaims = false;
                bearer.SaveToken = false;
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                    ValidateAudience = true, ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = jwt.GetKey(),
                    ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role"
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var userId)
                            || !Guid.TryParse(principal.FindFirstValue("sid"), out var familyId))
                        {
                            context.Fail("Invalid session.");
                            return;
                        }
                        var sender = context.HttpContext.RequestServices.GetRequiredService<ISender>();
                        if (!await sender.Send(new ValidateSessionQuery(userId, familyId,
                            principal.FindFirstValue("role"), principal.FindFirstValue("organization_id")),
                            context.HttpContext.RequestAborted)) context.Fail("Invalid session.");
                    }
                };
            });
        services.AddApiAuthorization();
        services.AddProblemDetails();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("administration", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
        });
        return services;
    }
}



