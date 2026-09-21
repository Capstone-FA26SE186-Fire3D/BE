using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Administration;
using Fire3D.Infrastructure.Email;

namespace Fire3D.API.Extensions;

public static class ApplicationExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAdministrationStore, AdministrationStore>();
        services.AddDefaultAWSOptions(configuration.GetAWSOptions()); services.AddAWSService<Amazon.S3.IAmazonS3>(); services.AddScoped<Fire3D.Application.Storage.IStorageService, Fire3D.Infrastructure.Storage.S3StorageService>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcReadStore, Fire3D.Infrastructure.Ifc.IfcReadStore>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcWriteStore, Fire3D.Infrastructure.Ifc.IfcWriteStore>();
        services.AddScoped<Fire3D.Application.Scenarios.IScenarioWriteStore, Fire3D.Infrastructure.Scenarios.ScenarioWriteStore>();
        services.AddScoped<Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.IRuntimeCatalogReadStore, Fire3D.Infrastructure.Scenarios.RuntimeCatalogReadStore>();
        services.AddScoped<Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.IPlaytestWriteStore, Fire3D.Infrastructure.Scenarios.PlaytestWriteStore>();
        services.AddScoped<Fire3D.Application.Releases.Commands.PublishRelease.IReleaseWriteStore, Fire3D.Infrastructure.Releases.ReleaseWriteStore>();
        services.AddScoped<Fire3D.Application.Buildings.IBuildingStore, Fire3D.Infrastructure.Buildings.BuildingStore>();
        services.AddScoped<Fire3D.Application.Buildings.Queries.GetTrainings.ITrainingReadStore, Fire3D.Infrastructure.Buildings.TrainingReadStore>();
        services.AddMediatR(options =>
        {
            options.RegisterServicesFromAssembly(typeof(Fire3D.Application.Authentication.Commands.FirebaseLogin.ExchangeFirebaseTokenCommand).Assembly);
            var licenseKey = configuration["MediatR:LicenseKey"];
            if (!string.IsNullOrWhiteSpace(licenseKey)) options.LicenseKey = licenseKey;
        });

        // Email – Mailgun
        services.AddOptions<MailgunOptions>()
            .Bind(configuration.GetSection(MailgunOptions.SectionName))
            .Validate(o => o.IsValid(), "Mailgun: ApiKey, Domain và From là bắt buộc.")
            .ValidateOnStart();
        services.AddHttpClient<IEmailService, MailgunEmailService>();

        // Auth email options (FrontendUrl cho link reset password)
        services.AddOptions<AuthEmailOptions>()
            .Bind(configuration.GetSection(AuthEmailOptions.SectionName));

        // FCM Notifications
        services.AddScoped<Fire3D.Application.Notifications.INotificationService, Fire3D.Infrastructure.Notifications.FcmNotificationService>();

        return services;
    }
}

