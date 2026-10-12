using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.SetAccountActive;
using Fire3D.Application.Administration.Queries.GetAccount;
using Fire3D.Application.Administration.Queries.ListAccounts;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Authentication.Commands.CreateAccount;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[Route("api/accounts")]
public sealed class AccountsController(ISender sender, IAvatarService? avatars = null) : AdministrationControllerBase
{
    /// <summary>
    /// Tạo mới một tài khoản (chỉ dành cho PlatformAdmin).
    /// </summary>
    /// <remarks>Body chứa mật khẩu dạng thô: chỉ gọi qua HTTPS, không log body. Role và tổ chức không đổi được sau khi tạo.</remarks>
    [HttpPost]
    [ProducesResponseType<AccountResponse>(201)]
    public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateAccountCommand(ActorId, request, NewCorrelationId()), ct);
        return result.IsSuccess ? Created($"/api/accounts/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy danh sách tài khoản (có phân trang và tìm kiếm).
    /// </summary>
    /// <remarks>sort: createdAt | updatedAt | email | fullName | lastLoginAt, thêm '-' để giảm dần; mặc định -createdAt. Giá trị khác → 400.
    /// avatarUrl luôn null ở danh sách; xem ảnh qua chi tiết tài khoản.</remarks>
    [HttpGet]
    public async Task<ActionResult<PageResponse<ManagedAccountResponse>>> List([FromQuery] AccountFilter filter, CancellationToken ct) =>
        Respond(await sender.Send(new ListAccountsQuery(ActorId, filter), ct));

    /// <summary>
    /// Lấy chi tiết thông tin một tài khoản.
    /// </summary>
    /// <remarks>ETag là profileRevision của tài khoản (cùng giá trị với /api/auth/me của chính người dùng).
    /// avatarUrl là signed URL 5 phút như /api/auth/me; null khi chưa có ảnh hoặc tài khoản không hoạt động.</remarks>
    [HttpGet("{id:guid}")]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<ManagedAccountResponse>> Get(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetAccountQuery(ActorId, id), ct);
        if (result.IsSuccess)
        {
            Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
            result = AuthResult<ManagedAccountResponse>.Ok(await WithAvatarAsync(result.Value, ct));
        }
        return Respond(result);
    }

    private async Task<ManagedAccountResponse> WithAvatarAsync(ManagedAccountResponse account, CancellationToken ct)
    {
        if (avatars is null) return account;
        var avatar = await avatars.GetAvatarAsync(account.Id, ct);
        return avatar.IsSuccess ? account with { AvatarUrl = avatar.Value!.Url } : account;
    }

    /// <summary>
    /// Kích hoạt hoặc vô hiệu hóa một tài khoản.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<ManagedAccountResponse>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        Respond(await sender.Send(new SetAccountActiveCommand(ActorId, id, request.IsActive, NewCorrelationId()), ct));
}
