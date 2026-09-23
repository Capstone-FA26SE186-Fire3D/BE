using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.CreateOrganization;
using Fire3D.Application.Administration.Commands.SetOrganizationActive;
using Fire3D.Application.Administration.Queries.GetOrganization;
using Fire3D.Application.Administration.Queries.ListOrganizations;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[Route("api/organizations")]
public sealed class OrganizationsController(ISender sender) : AdministrationControllerBase
{
    /// <summary>
    /// Tạo mới một tổ chức (Organization) - Chỉ dành cho PlatformAdmin.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<OrganizationResponse>> Create(CreateOrganizationRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateOrganizationCommand(ActorId, request, NewCorrelationId()), ct);
        return result.IsSuccess ? Created($"/api/organizations/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy danh sách các tổ chức (có phân trang và tìm kiếm).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PageResponse<OrganizationResponse>>> List([FromQuery] OrganizationFilter filter, CancellationToken ct) =>
        Respond(await sender.Send(new ListOrganizationsQuery(ActorId, filter), ct));

    /// <summary>
    /// Lấy thông tin chi tiết một tổ chức.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrganizationResponse>> Get(Guid id, CancellationToken ct) =>
        Respond(await sender.Send(new GetOrganizationQuery(ActorId, id), ct));

    /// <summary>
    /// Kích hoạt hoặc vô hiệu hóa một tổ chức.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<OrganizationResponse>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        Respond(await sender.Send(new SetOrganizationActiveCommand(ActorId, id, request.IsActive, NewCorrelationId()), ct));
}
