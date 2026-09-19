using System.Security.Claims;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.Login;
using Fire3D.Application.Authentication.Commands.RefreshToken;
using Fire3D.Application.Authentication.Commands.Logout;
using Fire3D.Application.Authentication.Commands.ForgotPassword;
using Fire3D.Application.Authentication.Commands.ResetPassword;
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
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request), ct);
        return result.IsSuccess
            ? Ok(new LoginResponse(result.Value!.AccessToken, result.Value.RefreshToken, result.Value.User))
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.Commands.Register.RegisterCommand(request), ct);
        return result.IsSuccess
            ? Ok(new LoginResponse(result.Value!.AccessToken, result.Value.RefreshToken, result.Value.User))
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken ct) =>
        Respond(await sender.Send(new RefreshTokenCommand(request.RefreshToken), ct));

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(Guid.Parse(User.FindFirstValue("sub")!),
            Guid.Parse(User.FindFirstValue("sid")!)), ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken ct)
    {
        var account = await sender.Send(new GetCurrentAccountQuery(Guid.Parse(User.FindFirstValue("sub")!)), ct);
        return account is null ? Unauthorized() : Ok(account);
    }

    /// <summary>
    /// Gửi email đặt lại mật khẩu. Luôn trả 204 để tránh email enumeration.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await sender.Send(new ForgotPasswordCommand(request.Email), ct);
        return NoContent(); // Luôn 204, kể cả email không tồn tại
    }

    /// <summary>
    /// Đặt lại mật khẩu bằng token nhận từ email.
    /// </summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new ResetPasswordCommand(request.Token, request.NewPassword), ct);
        return result.IsSuccess
            ? NoContent()
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    private ActionResult<TokenResponse> Respond(AuthResult<TokenResponse> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
}
