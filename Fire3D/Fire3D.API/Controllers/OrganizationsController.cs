using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.CreateOrganization;
using Fire3D.Application.Administration.Commands.SetOrganizationActive;
using Fire3D.Application.Administration.Commands.UpdateOrganization;
using Fire3D.Application.Authentication;
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
    [ProducesResponseType<OrganizationResponse>(201)]
    public async Task<ActionResult<OrganizationResponse>> Create(CreateOrganizationRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateOrganizationCommand(ActorId, request, NewCorrelationId()), ct);
        return result.IsSuccess ? Created($"/api/organizations/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy danh sách các tổ chức (có phân trang, tìm kiếm và sắp xếp).
    /// </summary>
    /// <remarks>sort: createdAt | updatedAt | name, thêm '-' để giảm dần; mặc định -createdAt. Giá trị khác → 400.</remarks>
    [HttpGet]
    public async Task<ActionResult<PageResponse<OrganizationResponse>>> List([FromQuery] OrganizationFilter filter, CancellationToken ct) =>
        Respond(await sender.Send(new ListOrganizationsQuery(ActorId, filter), ct));

    /// <summary>
    /// Lấy thông tin chi tiết một tổ chức.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<OrganizationResponse>> Get(Guid id, CancellationToken ct) =>
        WithETag(await sender.Send(new GetOrganizationQuery(ActorId, id), ct));

    /// <summary>
    /// Sửa tên, địa chỉ hoặc số điện thoại của một tổ chức (PlatformAdmin), cần If-Match.
    /// </summary>
    /// <remarks>Field bỏ qua giữ nguyên; null/rỗng bị từ chối. ETag dùng chung revision với /api/organizations/me.
    /// Thiếu If-Match 428, sai định dạng 400, cũ 412. Số điện thoại của tổ chức khác 409 ORGANIZATION_PHONE_EXISTS.
    /// Tổ chức inactive vẫn sửa được; đã xóa trả 404. Slug, trạng thái và gói không đổi ở đây.</remarks>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<OrganizationResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<OrganizationResponse>> Update(Guid id, [FromBody] UpdateOrganizationProfileRequest request,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateOrganizationCommand(ActorId, id, ifMatch, request, NewCorrelationId()), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }

    /// <summary>
    /// Kích hoạt hoặc vô hiệu hóa một tổ chức.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<OrganizationResponse>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        WithETag(await sender.Send(new SetOrganizationActiveCommand(ActorId, id, request.IsActive, NewCorrelationId()), ct));

    private ActionResult<OrganizationResponse> WithETag(AuthResult<OrganizationResponse> result)
    {
        if (result.IsSuccess) Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Respond(result);
    }
}
