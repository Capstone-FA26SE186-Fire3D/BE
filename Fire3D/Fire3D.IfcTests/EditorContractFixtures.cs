using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;

namespace Fire3D.Tests.Shared;

/// <summary>Loads the shared editor contract fixtures from BE/contracts/editor/v1 (also linked into AuthTests).</summary>
public static class EditorContractFixtures
{
    public static string ContractDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "editor", "v1");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("contracts/editor/v1 was not found above the test output directory.");
        }
    }

    public static JsonNode Load(string relative) => JsonNode.Parse(File.ReadAllText(Path.Combine(ContractDirectory, relative)))!;
    public static JsonNode Geometry() => Load("fixtures/geometry-metadata.valid.json");
    public static JsonElement GeometryElement() => JsonSerializer.SerializeToElement(Geometry());
    public static JsonObject State() => Load("fixtures/scenario-state.valid.json").AsObject();
    public static JsonObject CatalogFixture() => Load("fixtures/runtime-catalog.sample.json").AsObject();
    public static RuntimeCatalogDto Catalog()
    {
        var c = CatalogFixture();
        return new(c["runtimeVersion"]!.GetValue<string>(), c["protocolVersion"]!.GetValue<string>(), c["manifestSchemaVersion"]!.GetValue<string>(),
            c["capabilities"]!.DeepClone(), c["capabilityContracts"]!.DeepClone().AsObject());
    }
}
