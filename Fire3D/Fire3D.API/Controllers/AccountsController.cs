using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.SetAccountActive;
using Fire3D.Application.Administration.Queries.GetAccount;
using Fire3D.Application.Administration.Queries.ListAccounts;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.CreateAccount;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fire3D.API.Controllers;

[Route("api/accounts")]
public sealed class AccountsController(ISender sender) : AdministrationControllerBase
{
    /// <summary>
    /// Tạo mới một tài khoản (chỉ dành cho PlatformAdmin).
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("auth")]
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
    [HttpGet]
    public async Task<ActionResult<PageResponse<ManagedAccountResponse>>> List([FromQuery] AccountFilter filter, CancellationToken ct) =>
        Respond(await sender.Send(new ListAccountsQuery(ActorId, filter), ct));

    /// <summary>
    /// Lấy chi tiết thông tin một tài khoản.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ManagedAccountResponse>> Get(Guid id, CancellationToken ct) =>
        Respond(await sender.Send(new GetAccountQuery(ActorId, id), ct));

    /// <summary>
    /// Kích hoạt hoặc vô hiệu hóa một tài khoản.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<ManagedAccountResponse>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        Respond(await sender.Send(new SetAccountActiveCommand(ActorId, id, request.IsActive, NewCorrelationId()), ct));
}
