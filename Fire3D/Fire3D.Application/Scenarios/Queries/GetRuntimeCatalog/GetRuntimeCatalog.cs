using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Ifc;
using MediatR;
using System.Text.Json.Nodes;

namespace Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;

public sealed record GetRuntimeCatalogQuery(Guid ActorId) : IRequest<AuthResult<List<RuntimeCatalogDto>>>;

/// <summary>Active runtime. CapabilityContracts lists only contracts that pass the editor capability schema.</summary>
public sealed record RuntimeCatalogDto(string RuntimeVersion, string ProtocolVersion, string ManifestSchemaVersion, JsonNode Capabilities,
    JsonObject? CapabilityContracts = null);

public interface IRuntimeCatalogReadStore
{
    Task<List<RuntimeCatalogDto>> GetActiveCatalogAsync(CancellationToken ct);
    /// <summary>Latest active catalog row for one runtime version, or null.</summary>
    Task<RuntimeCatalogDto?> GetActiveRuntimeAsync(string runtimeVersion, CancellationToken ct);
}

public static class RuntimeCatalogContracts
{
    public static IReadOnlyDictionary<string, CapabilityContract> For(RuntimeCatalogDto? runtime) =>
        runtime is null ? new Dictionary<string, CapabilityContract>() : CapabilityContracts.Parse(runtime.Capabilities, runtime.CapabilityContracts);

    public static RuntimeCatalogDto PublishedView(RuntimeCatalogDto runtime)
    {
        var valid = For(runtime);
        var published = new JsonObject();
        foreach (var id in valid.Keys.Order(StringComparer.Ordinal)) published[id] = runtime.CapabilityContracts![id]!.DeepClone();
        return runtime with { CapabilityContracts = published };
    }
}

public sealed class GetRuntimeCatalogHandler(IAuthStore accounts, IRuntimeCatalogReadStore store)
    : IRequestHandler<GetRuntimeCatalogQuery, AuthResult<List<RuntimeCatalogDto>>>
{
    public async Task<AuthResult<List<RuntimeCatalogDto>>> Handle(GetRuntimeCatalogQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        var result = await store.GetActiveCatalogAsync(ct);
        return AuthResult<List<RuntimeCatalogDto>>.Ok(result.Select(RuntimeCatalogContracts.PublishedView).ToList());
    }
}
