using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Buildings.Queries.ListRevisions;
public sealed record ListRevisionsQuery(Guid ActorId, Guid BuildingId, int Page = 1, int PageSize = 20) : IRequest<AuthResult<PageResponse<RevisionResponse>>>;
