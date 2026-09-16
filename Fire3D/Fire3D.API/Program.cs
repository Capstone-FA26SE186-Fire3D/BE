using Fire3D.API.Extensions;
using Fire3D.Application.Authentication.Commands.BootstrapAdmin;
using MediatR;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

if (args.Contains("--check-database", StringComparer.Ordinal))
{
    Environment.ExitCode = await DatabaseConnectivityCheck.RunAsync(builder.Configuration, CancellationToken.None);
    return;
}

// Add services to the container.

builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddAccountAuthentication(builder.Configuration);


builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

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
if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles();
    app.MapOpenApi();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Fire3D API v1");
        options.RoutePrefix = "swagger";
        options.InjectStylesheet("../css/swagger-synthwave.css");
    });

}

// Cloud Run terminates TLS before forwarding HTTP to the container.
if (!builder.Configuration.GetValue<bool>("Hosting:BehindTlsProxy")) app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

public partial class Program;
