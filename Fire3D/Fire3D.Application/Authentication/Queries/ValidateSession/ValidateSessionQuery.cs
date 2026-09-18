using MediatR;

namespace Fire3D.Application.Authentication.Queries.ValidateSession;

public sealed record ValidateSessionQuery(Guid UserId, Guid FamilyId, string? Role, string? OrganizationId) : IRequest<bool>;
