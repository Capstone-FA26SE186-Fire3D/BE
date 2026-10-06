using Fire3D.Application.Administration;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Administration;
using Fire3D.Infrastructure.Email;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Amazon.Runtime;

namespace Fire3D.API.Extensions;

public static class ApplicationExtensions
{
    private static bool PayosHttps(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme=="https" && !uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddScoped<Fire3D.Application.Billing.IBillingService,Fire3D.Infrastructure.Billing.BillingService>();
        services.AddExceptionHandler<BillingExceptionHandler>();
        services.AddOptions<Fire3D.Application.Billing.PayosOptions>()
            .Bind(configuration.GetSection(Fire3D.Application.Billing.PayosOptions.Section))
            .Validate(options => !(options.Enabled || options.WorkerEnabled) ||
                (!string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ApiKey) && !string.IsNullOrWhiteSpace(options.ChecksumKey)), "PayOS requires ClientId, ApiKey and ChecksumKey when checkout or recovery is enabled.")
            .Validate(options => !options.Enabled || (PayosHttps(options.ReturnUrl) && PayosHttps(options.CancelUrl)), "PayOS ReturnUrl and CancelUrl must be absolute non-loopback HTTPS URLs.")
            .Validate(options => !(options.Enabled || options.WorkerEnabled) ||
                (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("PayosRequestExecutor")) && !string.IsNullOrWhiteSpace(configuration.GetConnectionString("PayosWebhookExecutor"))), "PayOS requires separate least-privilege request and webhook executor connections.")
            .Validate(options => options.PollSeconds is >= 5 and <= 300, "PayOS PollSeconds must be 5–300.")
            .ValidateOnStart();
        services.AddScoped<Fire3D.Application.Billing.IPayosProvider,Fire3D.Infrastructure.Billing.PayosSdkProvider>();
        services.AddHttpClient("fet3d-payos");
        services.AddScoped<Fire3D.Infrastructure.Billing.PayosExecutor>();
        services.AddScoped<Fire3D.Application.Billing.IPayosPayments,Fire3D.Infrastructure.Billing.PayosPayments>();
        services.AddHostedService<Fire3D.Infrastructure.Workers.PayosRecoveryWorker>();
        services.AddScoped<IAdministrationStore, AdministrationStore>();
        services.AddScoped<AvatarStore>();
        services.AddScoped<IAvatarStore>(provider => provider.GetRequiredService<AvatarStore>());
        services.AddScoped<IAvatarCleanupStore>(provider => provider.GetRequiredService<AvatarStore>());
        services.AddScoped<IAvatarService, AvatarService>();
        var awsOptions = configuration.GetAWSOptions();
        var accessKey = configuration["AWS:AccessKey"];
        var secretKey = configuration["AWS:SecretKey"];
        if (string.IsNullOrWhiteSpace(accessKey) != string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("AWS:AccessKey and AWS:SecretKey must be configured together.");
        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
            awsOptions.Credentials = new BasicAWSCredentials(accessKey, secretKey);
        services.AddDefaultAWSOptions(awsOptions); services.AddAWSService<Amazon.S3.IAmazonS3>(); services.AddScoped<Fire3D.Application.Storage.IStorageService, Fire3D.Infrastructure.Storage.S3StorageService>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcReadStore, Fire3D.Infrastructure.Ifc.IfcReadStore>();
        services.AddScoped<Fire3D.Application.Ifc.IEditorPreviewStore, Fire3D.Infrastructure.Ifc.EditorPreviewStore>();
        services.AddScoped<Fire3D.Application.Ifc.IPreviewDownloadSigner, Fire3D.Infrastructure.Storage.S3PreviewDownloadSigner>();
        services.AddScoped<Fire3D.Application.Ifc.IAnnotationStore, Fire3D.Infrastructure.Ifc.AnnotationStore>();
        services.AddOptions<Fire3D.Application.Ifc.ProcessingWorkerOptions>().Bind(configuration.GetSection("ProcessingWorker"))
            .Validate(o => o.PollSeconds is >= 1 and <= 60, "ProcessingWorker PollSeconds must be 1-60.")
            .Validate(o => !(o.WorkerApiEnabled || o.DispatcherEnabled) || o.MachineKey.Length is >= 32 and <= 512, "Processing worker requires a separate machine key of 32-512 characters.")
            .Validate(o => !o.WorkerApiEnabled || (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("ProcessingExecutor")) && o.AllowedToolchains.Length > 0), "Worker API requires restricted ProcessingExecutor connection and an explicit toolchain allowlist.")
            .Validate(o => !o.DispatcherEnabled || (Uri.TryCreate(o.WorkerUrl,UriKind.Absolute,out var url) && url.Scheme=="https" && string.IsNullOrEmpty(url.UserInfo)), "Dispatcher requires a configured HTTPS worker URL.")
            .ValidateOnStart();
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,Fire3D.API.Authorization.ProcessingWorkerAuthentication>(Fire3D.API.Authorization.ProcessingWorkerAuthentication.SchemeName,_=>{});
        services.AddScoped<Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore>();
        services.AddScoped<Fire3D.Application.Ifc.IRevisionProcessingStore>(p=>p.GetRequiredService<Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore>());
        services.AddScoped<Fire3D.Application.Ifc.IProcessingWorkerGate>(p=>p.GetRequiredService<Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore>());
        services.AddScoped<Fire3D.Application.Ifc.IProcessingDispatchGate>(p=>p.GetRequiredService<Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore>());
        services.AddHttpClient("processing-worker").ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false });
        services.AddHostedService<Fire3D.Infrastructure.Ifc.ProcessingDispatcher>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcWriteStore, Fire3D.Infrastructure.Ifc.IfcWriteStore>();
        services.AddOptions<Fire3D.Application.Ifc.IfcUploadOptions>().Bind(configuration.GetSection("IfcUpload"))
            .Validate(o => !o.Enabled || (o.MaxBytes is > 0 && o.CleanupEnabled), "IfcUpload requires explicit positive MaxBytes and CleanupEnabled=true when enabled.").ValidateOnStart();
        services.AddScoped<Fire3D.Application.Ifc.IIfcUploadStore, Fire3D.Infrastructure.Ifc.IfcUploadStore>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcSourceInspector, Fire3D.Infrastructure.Storage.IfcSourceInspector>();
        services.AddScoped<Fire3D.Application.Ifc.IIfcUploadService, Fire3D.Application.Ifc.IfcUploadService>();
        services.AddHostedService<Fire3D.Infrastructure.Ifc.IfcUploadCleanupWorker>();
        services.AddScoped<Fire3D.Application.Scenarios.IScenarioWriteStore, Fire3D.Infrastructure.Scenarios.ScenarioWriteStore>();
        services.AddScoped<Fire3D.Application.Scenarios.IScenarioPackageBuildStore, Fire3D.Infrastructure.Scenarios.ScenarioWriteStore>();
        services.AddScoped<Fire3D.Application.Scenarios.IScenarioReadStore, Fire3D.Infrastructure.Scenarios.ScenarioReadStore>();
        services.AddScoped<Fire3D.Application.Scenarios.Commands.RejectScenarioVersion.IScenarioReviewStore, Fire3D.Infrastructure.Scenarios.ScenarioReviewStore>();
        services.AddScoped<Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.IRuntimeCatalogReadStore, Fire3D.Infrastructure.Scenarios.RuntimeCatalogReadStore>();
        services.AddScoped<Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.IPlaytestWriteStore, Fire3D.Infrastructure.Scenarios.FailClosedPlaytestWriteStore>();
        services.AddScoped<Fire3D.Application.Releases.IReleaseStore, Fire3D.Infrastructure.Releases.FailClosedReleaseStore>();
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

        // FCM Notifications
        services.AddScoped<Fire3D.Application.Notifications.INotificationService, Fire3D.Infrastructure.Notifications.FcmNotificationService>();

        return services;
    }
}

