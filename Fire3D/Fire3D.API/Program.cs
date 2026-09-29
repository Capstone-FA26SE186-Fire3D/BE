using Fire3D.API.Extensions;
using Fire3D.API.Configuration;
using Fire3D.Application.Authentication.Commands.BootstrapAdmin;
using Fire3D.Application.Authentication;
using MediatR;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddAccountAuthentication(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(value, out var address))
            throw new InvalidOperationException("ForwardedHeaders:KnownProxies must contain only IP addresses.");
        options.KnownProxies.Add(address);
    }
});


builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
builder.Services.AddHealthChecks();

// Cấu hình Firebase Admin SDK
var firebaseCredentialJson = FirebaseAdminConfiguration.GetCredentialJson(
    builder.Configuration,
    builder.Environment.ContentRootPath);
if (!string.IsNullOrWhiteSpace(firebaseCredentialJson))
{
    FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
    {
#pragma warning disable CS0618
        Credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromJson(firebaseCredentialJson)
#pragma warning restore CS0618
    });
}
else
{
    // Log a warning or throw, depending on preference. We'll ignore for now to allow compiling without the file in some envs.
    Console.WriteLine("Warning: Firebase Admin credential is not configured.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var frontendUrls = CorsOriginConfiguration.GetAllowedOrigins(builder.Configuration);
        policy.WithOrigins(frontendUrls)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("ETag")
              .AllowCredentials();
    });
});

var app = builder.Build();

var authEmail = app.Services.GetRequiredService<IOptions<AuthEmailOptions>>().Value;
var verificationHost = new Uri(authEmail.GetVerificationPageBaseUrl()).Authority;
app.Logger.LogInformation(
    "FET3D startup configuration: Version={Version}, Environment={Environment}, VerificationHost={VerificationHost}, EmailWorkerEnabled={EmailWorkerEnabled}.",
    builder.Configuration["APP_VERSION"] ?? "unknown", builder.Environment.EnvironmentName,
    verificationHost, authEmail.WorkerEnabled);

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    // Credentials come from User Secrets / environment, never from command-line arguments.
    using var scope = app.Services.CreateScope();
    var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new BootstrapAdminCommand(
        builder.Configuration["BootstrapAdmin:Email"] ?? "",
        builder.Configuration["BootstrapAdmin:Password"] ?? ""), CancellationToken.None);
    Console.WriteLine(result.IsSuccess ? "Initial administrator created." : result.Error!.Message);
    Environment.ExitCode = result.IsSuccess ? 0 : 1;
    return;
}

app.UseExceptionHandler();
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/verify-email") || context.Request.Path.StartsWithSegments("/check-email"))
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; script-src 'self'; base-uri 'none'; form-action 'self'";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Fire3D API v1");
    options.RoutePrefix = "swagger";
    options.InjectStylesheet("/css/swagger-synthwave.css");
});


// Azure App Service handles SSL termination - no need for HTTPS redirect
// app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/health/version", (IConfiguration configuration) => Results.Ok(new
{
    version = configuration["APP_VERSION"] ?? "unknown"
}));
app.MapControllers();

app.Run();

public partial class Program;


