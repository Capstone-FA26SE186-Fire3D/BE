using Fire3D.Application.Administration;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

/// <summary>Custom JSON converters preserve omitted/null fields but hide their shape from schema generation.</summary>
public sealed class AuthProfileSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (context.JsonTypeInfo.Type == typeof(UpdateCurrentProfileRequest))
        {
            schema.Type = JsonSchemaType.Object;
            schema.Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["fullName"] = Text(200, "Omit/null keeps the current name; supplied text must be nonempty.", true),
                ["username"] = Text(30, "Omit/null keeps the current username. Lowercase unique a-z, digits, dot, underscore or hyphen; 3–30 characters.", true),
                ["dob"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Format = "date", Description = "YYYY-MM-DD, not in the future. Omit keeps the value; null clears it." },
                ["gender"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Integer | JsonSchemaType.Null,
                    Description = "Male (0), Female (1), Other (2), PreferNotToSay (3). Case-insensitive name or defined enum number; null clears, omit keeps." },
                ["phoneNumber"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null,
                    Description = "Uses registration phone normalization: 6–15 digits, optional initial +, spaces/hyphens/parentheses. Omit keeps; null clears." }
            };
            schema.Required = new HashSet<string>();
            schema.Description = "Partial personal profile. Send at least one mutable field. Role, tenant, email, status and avatar URL are not mutable here.";
        }
        else if (context.JsonTypeInfo.Type == typeof(UpdateOrganizationProfileRequest))
        {
            schema.Type = JsonSchemaType.Object;
            schema.Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["name"] = Text(255, "Omit keeps; supplied name cannot be null/empty."),
                ["address"] = Text(2000, "Omit keeps; supplied address cannot be null/empty."),
                ["phoneNumber"] = new OpenApiSchema { Type = JsonSchemaType.String,
                    Description = "Omit keeps; supplied phone cannot be null/empty. Registration phone normalization applies." }
            };
            schema.Required = new HashSet<string>();
            schema.Description = "Partial organization profile. Send at least one field. Null is rejected for every supplied field; slug, plan and status cannot be changed here.";
        }
        else if (context.JsonTypeInfo.Type == typeof(UserRole) || context.JsonTypeInfo.Type == typeof(UserGender))
        {
            var nullable = schema.Type.GetValueOrDefault().HasFlag(JsonSchemaType.Null);
            schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
            schema.Enum = Enum.GetNames(context.JsonTypeInfo.Type).Select(name => (JsonNode)JsonValue.Create(name)!).ToList();
            if (nullable) schema.Enum.Add(null!);
        }
        else if (context.JsonTypeInfo.Type == typeof(RegisterTraineeCommand) || context.JsonTypeInfo.Type == typeof(RegisterOrganizationCommand))
        {
            if (schema.Properties is not null) schema.Properties["gender"] = NullableGender();
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("registrationToken");
            if (schema.Properties is not null && schema.Properties.TryGetValue("registrationToken", out var proofSchema) && proofSchema is OpenApiSchema proof)
            {
                proof.Type = JsonSchemaType.String;
                proof.Description = "Single-use email proof returned by verify-otp; required, expires after 15 minutes. OTP itself is not a registrationToken.";
            }
            foreach (var name in new[] { "password", "confirmPassword" })
                if (schema.Properties is not null && schema.Properties.TryGetValue(name, out var passwordSchema) && passwordSchema is OpenApiSchema password)
                {
                    password.MinLength = 6; password.MaxLength = 128;
                    password.Description = name == "password" ? "6–128 characters, not all whitespace; do not trim." : "Must match password exactly.";
                }
        }
        else if (context.JsonTypeInfo.Type == typeof(GoogleOnboardingCompleteRequest) && schema.Properties is not null)
        {
            schema.Properties["gender"] = NullableGender();
            schema.Properties["accountType"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String, Enum = new List<JsonNode> { JsonValue.Create("Trainee")!, JsonValue.Create("OrganizationUser")! },
                Description = "Choose allowed self-registration type; server creates role/tenant. PlatformAdmin is forbidden."
            };
        }
        return Task.CompletedTask;
    }

    private static OpenApiSchema Text(int max, string description, bool nullable = false) => new()
    { Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String, MaxLength = max, Description = description };

    private static OpenApiSchema NullableGender() => new()
    {
        Type = JsonSchemaType.String | JsonSchemaType.Null,
        Enum = Enum.GetNames<UserGender>().Select(name => (JsonNode)JsonValue.Create(name)!).Append(null!).ToList(),
        Description = "Optional gender: Male, Female, Other or PreferNotToSay; null is allowed."
    };
}
