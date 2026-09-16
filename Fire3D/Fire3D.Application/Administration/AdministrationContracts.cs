using Fire3D.Domain.Enums;

namespace Fire3D.Application.Administration;

public sealed record CreateOrganizationRequest(string Name, string Slug);
public sealed record SetActiveRequest(bool? IsActive);
public sealed record OrganizationResponse(Guid Id, string Name, string Slug, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ManagedAccountResponse(Guid Id, string Email, string? FullName, UserRole Role,
    Guid? OrganizationId, bool IsActive, DateTime? LastLoginAt, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record OrganizationFilter(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null);
public sealed record AccountFilter(int Page = 1, int PageSize = 20, string? Search = null,
    bool? IsActive = null, UserRole? Role = null, Guid? OrganizationId = null);
