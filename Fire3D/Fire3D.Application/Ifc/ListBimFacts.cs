using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record BimFactResponse(Guid Id, Guid RevisionId, string IfcGlobalId, string EntityType, string PropertyPath, JsonElement? Value, string SourceHash, JsonElement QualityFlags, DateTime CreatedAt);
/// <summary>One element (an IfcSpace room) of the revision: its Name/LongName/Category facts and the union of their quality flags.</summary>
public sealed record BimFactElementResponse(string IfcGlobalId, string EntityType, JsonElement? Name, JsonElement? LongName, JsonElement? Category,
    IReadOnlyList<string> QualityFlags, string SourceHash, DateTime UpdatedAt);
/// <summary>Optional filters; QualityFlag matches every fact of an element that carries the flag (e.g. SPACE_UNNAMED).</summary>
public sealed record BimFactFilters(string? EntityType = null, string? IfcGlobalId = null, string? QualityFlag = null);
public sealed record ListBimFactsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20, BimFactFilters? Filters = null)
    : IRequest<AuthResult<PageResponse<BimFactResponse>>>;
public sealed record ListBimFactElementsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20, BimFactFilters? Filters = null)
    : IRequest<AuthResult<PageResponse<BimFactElementResponse>>>;
public sealed partial class ListBimFactsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListBimFactsQuery, AuthResult<PageResponse<BimFactResponse>>>, IRequestHandler<ListBimFactElementsQuery, AuthResult<PageResponse<BimFactElementResponse>>>
{
    public Task<AuthResult<PageResponse<BimFactResponse>>> Handle(ListBimFactsQuery request, CancellationToken ct) =>
        ReadAsync(request.ActorId, request.RevisionId, request.Page, request.PageSize, request.Filters, (tenant, filters) => store.ListBimFactsAsync(request.RevisionId, tenant, filters, request.Page, request.PageSize, ct), ct);
    public Task<AuthResult<PageResponse<BimFactElementResponse>>> Handle(ListBimFactElementsQuery request, CancellationToken ct) =>
        ReadAsync(request.ActorId, request.RevisionId, request.Page, request.PageSize, request.Filters, (tenant, filters) => store.ListBimFactElementsAsync(request.RevisionId, tenant, filters, request.Page, request.PageSize, ct), ct);

    private async Task<AuthResult<PageResponse<T>>> ReadAsync<T>(Guid actor, Guid revision, int page, int pageSize, BimFactFilters? filters,
        Func<Guid?, BimFactFilters, Task<PageResponse<T>?>> read, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, actor, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (revision == Guid.Empty || page is < 1 or > 100000 || pageSize is < 1 or > 100)
            return AuthResult<PageResponse<T>>.Fail("VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);
        filters ??= new();
        var errors = new Dictionary<string, string[]>();
        if (filters.EntityType is { } entity && !Code().IsMatch(entity)) errors["entityType"] = ["Use an IFC entity name such as IfcSpace."];
        if (filters.IfcGlobalId is { } guid && !GlobalId().IsMatch(guid)) errors["ifcGlobalId"] = ["Use an IFC GlobalId of 22 characters."];
        if (filters.QualityFlag is { } flag && !Code().IsMatch(flag)) errors["qualityFlag"] = ["Use a quality flag code such as SPACE_UNNAMED."];
        if (errors.Count > 0) return AuthResult<PageResponse<T>>.Fail("VALIDATION_ERROR", "Invalid BIM fact filter.", 400, errors);
        var result = await read(scope.Value!.OrganizationId, filters);
        return result is null ? AuthResult<PageResponse<T>>.Fail("NOT_FOUND", "Revision not found.", 404) : AuthResult<PageResponse<T>>.Ok(result);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,99}\z")]
    private static partial Regex Code();
    [GeneratedRegex(@"^[0-9A-Za-z_$]{22}\z")]
    private static partial Regex GlobalId();
}
