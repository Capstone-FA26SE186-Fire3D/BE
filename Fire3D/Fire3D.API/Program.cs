using Fire3D.API.Extensions;
using Fire3D.Application.Authentication.Commands.BootstrapAdmin;
using MediatR;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddAccountAuthentication(builder.Configuration);


builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
builder.Services.AddHealthChecks();

// Cáº¥u hÃ¬nh Firebase Admin SDK
// C?u hình Firebase Admin SDK t? appsettings.json
var firebaseConfig = builder.Configuration.GetSection("FirebaseAdmin").Get<Dictionary<string, string>>();
if (firebaseConfig != null && firebaseConfig.Any())
{
    // Ð?m b?o private_key d?c t? appsettings s? x? lý dúng các ký t? xu?ng dòng
    if (firebaseConfig.ContainsKey("private_key") && firebaseConfig["private_key"] != null)
    {
        firebaseConfig["private_key"] = firebaseConfig["private_key"].Replace("\\n", "\n");
    }

    var json = System.Text.Json.JsonSerializer.Serialize(firebaseConfig);
    FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
    {
#pragma warning disable CS0618
        Credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromJson(json)
#pragma warning restore CS0618
    });
    Console.WriteLine("Firebase Admin SDK initialized from appsettings.");
}
else
{
    Console.WriteLine("Warning: FirebaseAdmin section not found in appsettings.json.");
});
}
else
{
    // Log a warning or throw, depending on preference. We'll ignore for now to allow compiling without the file in some envs.
    Console.WriteLine("Warning: firebase-admin.json not found.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var frontendUrl = builder.Configuration["Auth:FrontendUrl"] ?? "http://localhost:3000";
        policy.WithOrigins(frontendUrl)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

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

// Configure the HTTP request pipeline.
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
app.MapControllers();

app.Run();

public partial class Program;


