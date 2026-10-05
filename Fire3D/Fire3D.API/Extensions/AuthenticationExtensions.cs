using System.IdentityModel.Tokens.Jwt;
using Fire3D.API.Authorization;
using System.Security.Claims;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Queries.ValidateSession;
using MediatR;
using Fire3D.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Fire3D.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddAccountAuthentication(this IServiceCollection services, IConfiguration configuration,
        bool allowLoopbackEmailUrls = false)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(x => x.IsValid(), "Configure Jwt issuer, audience, a base64 signing key of at least 32 random bytes, and valid token lifetimes.")
            .ValidateOnStart();
        services.AddOptions<AuthTokenCleanupOptions>().Bind(configuration.GetSection(AuthTokenCleanupOptions.SectionName))
            .Validate(x => x.IsValid(), "Configure AuthTokenCleanup with valid interval, retention, batch size, and batch count values.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<IAuthStore, AuthStore>();
        services.AddScoped<IGoogleOnboardingService, GoogleOnboardingService>();
        services.AddScoped<IRefreshTokenCleanupStore, RefreshTokenCleanupStore>();
        services.AddScoped<ILocalPasswordReset, LocalPasswordReset>();
        services.AddScoped<Fire3D.Application.Authentication.Services.Fire3DSessionIssuer>();
        services.AddSingleton<IFirebaseGoogleTokenVerifier, FirebaseGoogleTokenVerifier>();
        services.AddHttpClient<Fire3D.Application.Authentication.Abstractions.IIdentityProvider, FirebaseIdentityProvider>(
            client => client.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
        services.AddOptions<AuthEmailOptions>()
            .Bind(configuration.GetSection(AuthEmailOptions.SectionName))
            .Validate(options => options.IsValidForEnvironment(allowLoopbackEmailUrls),
                "Configure AuthEmail:FrontendUrl and AuthEmail:VerificationUrl as HTTPS public base URLs. HTTP loopback URLs are allowed only in Development.")
            .ValidateOnStart();
        services.AddSingleton<IFirebaseResetAdmin, FirebaseResetAdmin>();
        services.AddScoped<IPasswordResetStore, PasswordResetStore>();
        services.AddScoped<IPasswordResetQueue, PasswordResetQueue>();
        services.AddScoped<IEmailVerificationQueue, EmailVerificationQueue>();
        services.AddScoped<RegistrationOtpStore>();
        services.AddScoped<IRegistrationOtpService>(provider => provider.GetRequiredService<RegistrationOtpStore>());
        services.AddScoped<IRegistrationOtpDeliveryQueue>(provider => provider.GetRequiredService<RegistrationOtpStore>());
        services.AddExceptionHandler<PasswordResetExceptionHandler>();
        services.AddHttpClient<IPasswordResetProvider, FirebasePasswordResetProvider>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers();
        services.AddHostedService<Fire3D.Infrastructure.Workers.PasswordResetWorker>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.EmailVerificationWorker>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.RegistrationOtpEmailWorker>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.PendingRegistrationCleanupWorker>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.AvatarCleanupWorker>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.RefreshTokenCleanupWorker>();

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
            options.AddPolicy("auth-refresh", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.OnRejected = async (rejection, ct) =>
            {
                var context = rejection.HttpContext;
                if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName != "auth-refresh") return;
                var retry = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
                    ? Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) : 60;
                context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many refresh requests. Try again after the indicated delay.",
                    Type = "https://www.rfc-editor.org/rfc/rfc6585#section-4",
                    Extensions = { ["code"] = "AUTH_REFRESH_RATE_LIMITED",
                        ["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier }
                }, options: null, contentType: "application/problem+json", cancellationToken: ct);
            };
            options.AddPolicy("administration", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
        });
        return services;
    }
}



