using Fire3D.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Authorization;

/// <summary>
/// Deny by default for FireSafetyExpert (BE#211). An expert belongs to no organization and may only use the thẩm định queue
/// (/api/expert) and account self-service (/api/auth: me, logout, devices, change-password; /api/me: avatar, password, Google link).
/// Many controllers accept any signed-in role and leave the role and tenant checks to handlers and SQL gates, which were written when
/// every non-PlatformAdmin was an OrganizationUser or a Trainee. This guard answers 403 FORBIDDEN for every other /api route before
/// any of that code runs, so a new route is closed to experts unless it is added to <see cref="AllowedPrefixes"/>.
/// </summary>
public static class ExpertRouteGuard
{
    public static readonly string[] AllowedPrefixes = ["/api/expert", "/api/auth", "/api/me"];

    public static bool Allows(PathString path) =>
        !path.StartsWithSegments("/api") || AllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseExpertRouteGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.IsInRole(nameof(UserRole.FireSafetyExpert)) && !Allows(context.Request.Path))
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "FireSafetyExpert accounts can only use the review queue (/api/expert) and their own account.",
                Instance = context.Request.Path
            };
            problem.Extensions["code"] = "FORBIDDEN";
            problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted);
            return;
        }
        await next();
    });
}
