using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record BimFactResponse(Guid Id, Guid RevisionId, string IfcGlobalId, string EntityType, string PropertyPath, JsonElement? Value, string SourceHash, JsonElement QualityFlags, DateTime CreatedAt);
public sealed record ListBimFactsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<BimFactResponse>>>;
public sealed class ListBimFactsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListBimFactsQuery, AuthResult<PageResponse<BimFactResponse>>>
{
    public async Task<AuthResult<PageResponse<BimFactResponse>>> Handle(ListBimFactsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<BimFactResponse>>.Fail("VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);
        var result = await store.ListBimFactsAsync(request.RevisionId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null ? AuthResult<PageResponse<BimFactResponse>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<PageResponse<BimFactResponse>>.Ok(result);
    }
}
