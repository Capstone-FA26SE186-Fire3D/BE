using System.IdentityModel.Tokens.Jwt;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Authorization;

/// <summary>
/// BE#235: a session opened by the first sign-in of an account created by PlatformAdmin (first_login_step ChangePassword) may only
/// change the password, read /api/auth/me, refresh its tokens and log out (BE#245: a client that sends its bearer on refresh is not refused); every other /api route answers 403 PASSWORD_CHANGE_REQUIRED before any handler
/// runs. Changing the password clears the step and revokes every session. The step is read from the account the bearer session was
/// just validated against (no extra query).
/// </summary>
public static class PasswordChangeGuard
{
    public static bool Allows(string method, PathString path) =>
        !path.StartsWithSegments("/api")
        || (HttpMethods.IsPost(method) && (path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/auth/logout-all", StringComparison.OrdinalIgnoreCase)))
        || (HttpMethods.IsGet(method) && path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UsePasswordChangeGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true && !Allows(context.Request.Method, context.Request.Path)
            && Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            var store = context.RequestServices.GetRequiredService<IAuthStore>();
            var actor = store.ValidatedSessionActor(userId) ?? await store.FindUserAsync(userId, context.RequestAborted);
            if (actor?.FirstLoginStep == FirstLoginSteps.ChangePassword)
            {
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Change the initial password before using this account (POST /api/auth/change-password).",
                    Instance = context.Request.Path
                };
                problem.Extensions["code"] = "PASSWORD_CHANGE_REQUIRED";
                problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted);
                return;
            }
        }
        await next();
    });
}
