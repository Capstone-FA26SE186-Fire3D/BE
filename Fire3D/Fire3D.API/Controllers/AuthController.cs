using System.Security.Claims;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RefreshToken;
using Fire3D.Application.Authentication.Commands.Logout;
using Fire3D.Application.Authentication.Queries.GetCurrentAccount;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Đăng nhập bằng Firebase ID Token.
    /// </summary>
    [HttpPost("login-firebase")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> LoginFirebase([FromBody] string firebaseIdToken, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.Commands.FirebaseLogin.ExchangeFirebaseTokenCommand(firebaseIdToken), ct);
        if (!result.IsSuccess) 
        {
            return Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Cấp lại Access Token mới dựa vào Refresh Token hợp lệ.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken ct) =>
        Respond(await sender.Send(new RefreshTokenCommand(request.RefreshToken), ct));

    /// <summary>
    /// Đăng xuất khỏi hệ thống, thu hồi Refresh Token hiện tại.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(Guid.Parse(User.FindFirstValue("sub")!),
            Guid.Parse(User.FindFirstValue("sid")!)), ct);
        return NoContent();
    }

    /// <summary>
    /// Lấy thông tin tài khoản của phiên đăng nhập hiện tại.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken ct)
    {
        var account = await sender.Send(new GetCurrentAccountQuery(Guid.Parse(User.FindFirstValue("sub")!)), ct);
        return account is null ? Unauthorized() : Ok(account);
    }

    private ActionResult<TokenResponse> Respond(AuthResult<TokenResponse> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
}

    /// <summary>
    /// Gửi email đặt lại mật khẩu. Luôn trả 204 để tránh email enumeration.
    /// </summary>
