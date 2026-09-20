using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;
using System.Text.Json.Nodes;

namespace Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;

public sealed record GetRuntimeCatalogQuery(Guid ActorId) : IRequest<AuthResult<List<RuntimeCatalogDto>>>;

public sealed record RuntimeCatalogDto(string RuntimeVersion, string ProtocolVersion, string ManifestSchemaVersion, JsonNode Capabilities);

public interface IRuntimeCatalogReadStore
{
    Task<List<RuntimeCatalogDto>> GetActiveCatalogAsync(CancellationToken ct);
}

public sealed class GetRuntimeCatalogHandler(IAuthStore accounts, IRuntimeCatalogReadStore store)
    : IRequestHandler<GetRuntimeCatalogQuery, AuthResult<List<RuntimeCatalogDto>>>
{
    public async Task<AuthResult<List<RuntimeCatalogDto>>> Handle(GetRuntimeCatalogQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        var result = await store.GetActiveCatalogAsync(ct);
        return AuthResult<List<RuntimeCatalogDto>>.Ok(result);
    }
}
