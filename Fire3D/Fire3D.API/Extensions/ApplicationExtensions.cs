using Fire3D.Application.Authentication.Commands.Login;

namespace Fire3D.API.Extensions;

public static class ApplicationExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMediatR(options =>
        {
            options.RegisterServicesFromAssemblyContaining<LoginCommand>();
            var licenseKey = configuration["MediatR:LicenseKey"];
            if (!string.IsNullOrWhiteSpace(licenseKey)) options.LicenseKey = licenseKey;
        });
        return services;
    }
}
