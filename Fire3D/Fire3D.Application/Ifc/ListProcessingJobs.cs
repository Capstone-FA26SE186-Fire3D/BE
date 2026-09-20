using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record ListProcessingJobsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<ProcessingJobResponse>>>;
public sealed class ListProcessingJobsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListProcessingJobsQuery, AuthResult<PageResponse<ProcessingJobResponse>>>
{
    public async Task<AuthResult<PageResponse<ProcessingJobResponse>>> Handle(ListProcessingJobsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<ProcessingJobResponse>>.Fail("VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);
        var page = await store.ListJobsAsync(request.RevisionId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return page is null ? AuthResult<PageResponse<ProcessingJobResponse>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<PageResponse<ProcessingJobResponse>>.Ok(page);
    }
}
