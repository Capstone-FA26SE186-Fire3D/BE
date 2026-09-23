using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Ifc;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnnotationItem(Guid Id, string IfcGlobalId, string Label, string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnnotationData(IReadOnlyList<AnnotationItem> Items);
public sealed record AnnotationSnapshot(Guid RevisionId, Guid? Id, int Version, JsonElement Data,
    string? Provenance, Guid? CreatedBy, DateTimeOffset? CreatedAt)
{
    public string ETag => $"\"{Version.ToString(CultureInfo.InvariantCulture)}\"";
}
public interface IAnnotationStore
{
    Task<AnnotationSnapshot?> ReadAsync(Guid revisionId, Guid? tenant, CancellationToken ct);
    Task<AuthResult<AnnotationSnapshot>> AppendAsync(Guid actorId, Guid revisionId, int expectedVersion, AnnotationData data, CancellationToken ct);
}
public sealed record GetAnnotationsQuery(Guid ActorId, Guid RevisionId) : IRequest<AuthResult<AnnotationSnapshot>>;
public sealed class GetAnnotationsHandler(IAuthStore accounts, IAnnotationStore store)
    : IRequestHandler<GetAnnotationsQuery, AuthResult<AnnotationSnapshot>>
{
    public async Task<AuthResult<AnnotationSnapshot>> Handle(GetAnnotationsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        var snapshot = await store.ReadAsync(request.RevisionId, scope.Value!.OrganizationId, ct);
        return snapshot is null ? AuthResult<AnnotationSnapshot>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<AnnotationSnapshot>.Ok(snapshot);
    }
}
public sealed record SaveAnnotationsCommand(Guid ActorId, Guid RevisionId, string? IfMatch, AnnotationData Data)
    : IRequest<AuthResult<AnnotationSnapshot>>;
public sealed class SaveAnnotationsHandler(IAuthStore accounts, IAnnotationStore store)
    : IRequestHandler<SaveAnnotationsCommand, AuthResult<AnnotationSnapshot>>
{
    public async Task<AuthResult<AnnotationSnapshot>> Handle(SaveAnnotationsCommand request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return AuthResult<AnnotationSnapshot>.Fail("PRECONDITION_REQUIRED", "Send the ETag from GET in If-Match.", 428);
        var tag = request.IfMatch.Trim();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"'
            || !int.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version == int.MaxValue)
            return AuthResult<AnnotationSnapshot>.Fail("VALIDATION_ERROR", "If-Match must contain one quoted non-negative version.", 400);
        var items = request.Data?.Items;
        if (request.RevisionId == Guid.Empty || items is null || items.Count > 500
            || items.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.IfcGlobalId)
                || x.IfcGlobalId.Length > 255 || string.IsNullOrWhiteSpace(x.Label) || x.Label.Length > 200 || x.Note?.Length > 2000)
            || items.Select(x => x.Id).Distinct().Count() != items.Count)
            return AuthResult<AnnotationSnapshot>.Fail("VALIDATION_ERROR", "Provide up to 500 uniquely identified IFC annotations with label <=200 and note <=2000 characters.", 400);
        return await store.AppendAsync(request.ActorId, request.RevisionId, version, request.Data!, ct);
    }
}
