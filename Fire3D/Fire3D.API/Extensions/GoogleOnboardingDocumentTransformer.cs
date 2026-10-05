using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

// Run after schema generation: the property-specific converter must not reuse the authorization role enum.
public sealed class GoogleOnboardingDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        if (document.Components?.Schemas?.TryGetValue("GoogleOnboardingCompleteRequest", out var request) == true
            && request is OpenApiSchema schema && schema.Properties is not null)
            schema.Properties["accountType"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = new[] { "trainee", "organization", "Trainee", "OrganizationUser" }
                    .Select(x => (JsonNode)JsonValue.Create(x)!).ToList(),
                Description = "Canonical trainee/organization; role-name aliases retained. Numbers and PlatformAdmin rejected with errors.accountType."
            };
        return Task.CompletedTask;
    }
}
