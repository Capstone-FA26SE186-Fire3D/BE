using Fire3D.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace Fire3D.API.Authorization;

public static class AuthorizationPolicies
{
    public const string PlatformAdministration = nameof(PlatformAdministration);

    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PlatformAdministration, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(nameof(UserRole.PlatformAdmin)));
        });
        // Secure every controller by default, including future controllers.
        // Only actions explicitly marked AllowAnonymous can bypass this filter.
        // Development Swagger/OpenAPI endpoints are outside the controller filter.
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build())));
        return services;
    }
}
