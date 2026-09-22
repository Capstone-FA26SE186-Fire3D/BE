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
    /// Đăng ký tài khoản (Tạo trên Firebase + DB)
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Register([FromBody] Fire3D.Application.Authentication.Commands.RegisterUser.RegisterUserCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
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

    /// <summary>
    /// Đăng ký thiết bị và FCM Token để nhận Push Notification.
    /// </summary>
    [HttpPut("devices")]
    [Authorize]
    public async Task<IActionResult> RegisterDevice([FromBody] Fire3D.Application.Users.Commands.RegisterDevice.RegisterDeviceCommand request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue("sub")!);
        await sender.Send(request with { UserId = userId }, ct);
        return Ok();
    }

    private ActionResult<TokenResponse> Respond(AuthResult<TokenResponse> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });

    /// <summary>
    /// Gửi hướng dẫn đặt lại mật khẩu qua email. Không cần Bearer.
    /// </summary>
    /// <remarks>Body: email hợp lệ, tối đa 254 ký tự. Trả 202 cho cả email có và không có tài khoản;
    /// 202 chỉ xác nhận đã nhận yêu cầu, không đảm bảo email đã được gửi. Tài khoản chỉ dùng Google
    /// không được cấp mật khẩu mới qua luồng này. Email được xử lý nền và giới hạn tần suất.</remarks>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] Fire3D.Application.Authentication.Commands.ForgotPassword.ForgotPasswordCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        if (!result.IsSuccess) return ResetProblem(result.Error!);
        return Accepted(new { message = "Nếu tài khoản đủ điều kiện, hướng dẫn đặt lại mật khẩu sẽ được gửi đến email của bạn." });
    }

    /// <summary>Đổi mật khẩu bằng mã Firebase trong email và thu hồi các phiên Fire3D.</summary>
    /// <remarks>Không cần Bearer. Body: oobCode, newPassword (12–128 ký tự). 400: mã/mật khẩu sai;
    /// 409: reset đang chờ xử lý; 503: provider chưa khả dụng. Khi kết quả provider không rõ, tài khoản
    /// bị chặn cấp phiên mới đến khi recovery hoàn tất. Không gửi mật khẩu/mã reset vào log.</remarks>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] Fire3D.Application.Authentication.Commands.ResetPassword.ResetPasswordCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }
    private ObjectResult ResetProblem(AuthError error) => Problem(statusCode:error.Status,title:error.Message,
        extensions:new Dictionary<string,object?> { ["code"] = error.Code });
}
