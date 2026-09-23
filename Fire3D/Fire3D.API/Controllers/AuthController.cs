using Fire3D.API.Authorization;
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
    /// <summary>Đăng nhập email/password do BE quản lý. Không cần Bearer.</summary>
    /// <remarks>Body: email, password. BE kiểm tra hash trong PostgreSQL; không gọi Firebase.
    /// 200: accessToken, refreshToken, user; 400: dữ liệu sai; 401: sai email/mật khẩu;
    /// 403: tài khoản/tổ chức bị vô hiệu hóa; 429: vượt giới hạn yêu cầu.</remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<LoginResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> Login(
        [FromBody] Fire3D.Application.Authentication.Commands.LoginWithPassword.LoginWithPasswordCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? Ok(result.Value) : ResetProblem(result.Error!);
    }

    /// <summary>
    /// Đăng nhập bằng Firebase ID Token.
    /// </summary>
    [HttpPost("login-firebase")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<TokenResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
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
    /// Đăng ký tài khoản thường. BE hash mật khẩu và tạo hồ sơ Trainee trong PostgreSQL.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType<AccountResponse>(201)]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AccountResponse>> Register([FromBody] Fire3D.Application.Authentication.Commands.RegisterUser.RegisterUserCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        if (!result.IsSuccess)
        {
            return Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        }
        return Created($"/api/accounts/{result.Value!.Id}", result.Value);
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
        await sender.Send(new LogoutCommand(User.GetActorId(), User.GetSessionFamilyId()), ct);
        return NoContent();
    }

    /// <summary>
    /// Lấy thông tin tài khoản của phiên đăng nhập hiện tại.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken ct)
    {
        var account = await sender.Send(new GetCurrentAccountQuery(User.GetActorId()), ct);
        return account is null ? Unauthorized() : Ok(account);
    }

    /// <summary>Updates the current account profile. Role, organization, identity provider and password are not mutable here.</summary>
    [HttpPatch("me")]
    [Authorize]
    [ProducesResponseType<AccountResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<ActionResult<AccountResponse>> UpdateMe(
        Fire3D.Application.Authentication.Commands.RegisterUser.UpdateCurrentProfileRequest request, CancellationToken ct)
    {
        var result = await sender.Send(
            new Fire3D.Application.Authentication.Commands.RegisterUser.UpdateCurrentProfileCommand(User.GetActorId(), request), ct);
        return result.IsSuccess ? Ok(result.Value) : ResetProblem(result.Error!);
    }

    /// <summary>
    /// Đăng ký thiết bị và FCM Token để nhận Push Notification.
    /// </summary>
    [HttpPut("devices")]
    [Authorize]
    public async Task<IActionResult> RegisterDevice([FromBody] Fire3D.Application.Users.Commands.RegisterDevice.RegisterDeviceCommand request, CancellationToken ct)
    {
        var userId = User.GetActorId();
        var result = await sender.Send(request with { UserId = userId }, ct);
        return result.IsSuccess ? Ok() : ResetProblem(result.Error!);
    }

    /// <summary>Revokes push delivery for one installation owned by the current account.</summary>
    /// <remarks>Idempotent: an unknown or already revoked device still returns 204. It does not revoke Fire3D sessions.</remarks>
    [HttpDelete("devices/{deviceUuid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> RevokeDevice(string deviceUuid, CancellationToken ct)
    {
        var result = await sender.Send(
            new Fire3D.Application.Users.Commands.RegisterDevice.RevokeDeviceCommand(User.GetActorId(), deviceUuid), ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }

    private ActionResult<TokenResponse> Respond(AuthResult<TokenResponse> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });

    /// <summary>
    /// Gửi hướng dẫn đặt lại mật khẩu qua email. Không cần Bearer.
    /// </summary>
    /// <remarks>Body: email hợp lệ, tối đa 254 ký tự. Trả 202 cho cả email có và không có tài khoản;
    /// 202 only acknowledges the request; it does not guarantee delivery. Only accounts with a local
    /// password are eligible for reset; Google-only accounts use the separate set-password/link flow.</remarks>
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

    /// <summary>Đổi mật khẩu local bằng token email và thu hồi tất cả phiên Fire3D.</summary>
    /// <remarks>Không cần Bearer. Body: token (64 ký tự hex), newPassword (12–128 ký tự).
    /// Token hết hạn sau 30 phút, chỉ dùng một lần. 400: token/mật khẩu sai; 204: thành công.
    /// Không gửi mật khẩu/token vào log. Token Firebase oobCode cũ không dùng được.</remarks>
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

    /// <summary>Changes the signed-in user's local password and revokes every Fire3D refresh session.</summary>
    /// <remarks>
    /// Requires Bearer authentication. Body requires currentPassword and newPassword (12–128 characters).
    /// The update, invalidation of unused reset tokens, refresh-session revocation, and audit record commit together.
    /// Successful callers must sign in again. This endpoint does not send email and does not accept a Firebase oobCode.
    /// </remarks>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] Fire3D.Application.Authentication.Commands.ChangePassword.ChangePasswordRequest request,
        CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.Commands.ChangePassword.ChangePasswordCommand(
            User.GetActorId(), request.CurrentPassword, request.NewPassword), ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }
    private ObjectResult ResetProblem(AuthError error) => Problem(statusCode:error.Status,title:error.Message,
        extensions:new Dictionary<string,object?> { ["code"] = error.Code });
}
