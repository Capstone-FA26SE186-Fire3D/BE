using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Ifc;

public sealed record RevisionProcessingLogResponse(
    Guid Id,
    Guid RevisionId,
    Guid JobId,
    string Step,
    string Status,
    string? Message,
    int? DurationMs,
    int AttemptNumber,
    DateTime LoggedAt);

public sealed record ListRevisionProcessingLogsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<RevisionProcessingLogResponse>>>;

public sealed class ListRevisionProcessingLogsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListRevisionProcessingLogsQuery, AuthResult<PageResponse<RevisionProcessingLogResponse>>>
{
    public async Task<AuthResult<PageResponse<RevisionProcessingLogResponse>>> Handle(
        ListRevisionProcessingLogsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<RevisionProcessingLogResponse>>.Fail(
                "VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);

        var result = await store.ListRevisionProcessingLogsAsync(
            request.RevisionId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null
            ? AuthResult<PageResponse<RevisionProcessingLogResponse>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<PageResponse<RevisionProcessingLogResponse>>.Ok(result);
    }
}
