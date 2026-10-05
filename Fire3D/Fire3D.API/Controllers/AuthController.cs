using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RefreshToken;
using Fire3D.Application.Authentication.Commands.Logout;
using Fire3D.Application.Authentication.Queries.GetCurrentAccount;
using Fire3D.Application.Authentication.Avatar;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(ISender sender, IAvatarService? avatars = null) : ControllerBase
{
    /// <summary>Đăng nhập email/password do BE quản lý. Không cần Bearer.</summary>
    /// <remarks>Body: email, password. BE kiểm tra hash trong PostgreSQL; không gọi Firebase.
    /// 200: accessToken, refreshToken, user; 400: dữ liệu sai; 401: sai email/mật khẩu;
    /// 403: tài khoản/tổ chức bị vô hiệu hóa; 429: vượt giới hạn yêu cầu.</remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
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
    public async Task<ActionResult> LoginFirebase([FromBody] string firebaseIdToken, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.Commands.FirebaseLogin.ExchangeFirebaseTokenCommand(firebaseIdToken), ct);
        if (!result.IsSuccess) 
        {
            return Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        }
        return Ok(result.Value);
    }

    /// <summary>Deprecated Trainee registration alias. New clients should use /api/auth/register/trainee.</summary>
    [HttpPost("register")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<AccountResponse>(201)]
    [AllowAnonymous]
    public Task<ActionResult<AccountResponse>> Register(
        [FromBody] Fire3D.Application.Authentication.Commands.SelfRegistration.RegisterTraineeCommand command,
        CancellationToken ct) => RegisterTrainee(command, ct);

    /// <summary>Registers a Trainee with a globally unique lowercase username.</summary>
    [HttpPost("register/trainee")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<AccountResponse>(201)]
    [AllowAnonymous]
    public async Task<ActionResult<AccountResponse>> RegisterTrainee(
        [FromBody] Fire3D.Application.Authentication.Commands.SelfRegistration.RegisterTraineeCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        if (!result.IsSuccess)
        {
            return ResetProblem(result.Error!);
        }
        return Created($"/api/accounts/{result.Value!.Id}", result.Value);
    }

    /// <summary>Registers a new organization and its initial OrganizationUser owner atomically.</summary>
    [HttpPost("register/organization")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<AccountResponse>(201)]
    [AllowAnonymous]
    public async Task<ActionResult<AccountResponse>> RegisterOrganization(
        [FromBody] Fire3D.Application.Authentication.Commands.SelfRegistration.RegisterOrganizationCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        if (!result.IsSuccess)
            return ResetProblem(result.Error!);
        return Created($"/api/accounts/{result.Value!.Id}", result.Value);
    }

    /// <summary>Resends a six-digit registration OTP.</summary>
    /// <remarks>
    /// After the one-minute cooldown, this invalidates the previous registration OTP and registration proof for
    /// the email. New registrations use <c>registration/request-otp</c> for the first code. An existing email returns 409 EMAIL_EXISTS.
    /// </remarks>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> ResendVerification(
        [FromBody] Fire3D.Application.Authentication.RequestRegistrationOtpRequest request,
        CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.RequestRegistrationOtpCommand(
            request.Email, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        return RespondToRegistrationOtpRequest(result);
    }

    /// <summary>Verifies a legacy account-verification link.</summary>
    /// <remarks>Deprecated for new registrations, which verify an OTP before an account is created.</remarks>
    [Obsolete("New registrations must use /api/auth/registration/verify-otp.")]
    [HttpPost("verify-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> VerifyEmail([FromBody] Fire3D.Application.Authentication.VerifyEmailCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }

    /// <summary>Requests a six-digit email code before a new account is created.</summary>
    /// <remarks>
    /// This endpoint never creates a user or organization. New emails return 202; registered emails return 409
    /// EMAIL_EXISTS with errors.email, without queuing email. Codes expire after ten minutes. The FE keeps its form
    /// in memory, verifies the OTP, then submits the full form with registrationToken to the appropriate register route.
    /// </remarks>
    [HttpPost("registration/request-otp")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> RequestRegistrationOtp(
        [FromBody] Fire3D.Application.Authentication.RequestRegistrationOtpRequest request,
        CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Authentication.RequestRegistrationOtpCommand(
            request.Email, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        return RespondToRegistrationOtpRequest(result);
    }

    /// <summary>Verifies a six-digit registration code and returns a short-lived registration proof.</summary>
    [HttpPost("registration/verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType<RegistrationOtpVerificationResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<ActionResult<RegistrationOtpVerificationResponse>> VerifyRegistrationOtp(
        [FromBody] Fire3D.Application.Authentication.VerifyRegistrationOtpCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? Ok(result.Value) : ResetProblem(result.Error!);
    }

    /// <summary>
    /// Cấp lại Access Token mới dựa vào Refresh Token hợp lệ.
    /// </summary>
    /// <remarks>10 requests per connection IP per minute on each instance. 429 includes Retry-After and AUTH_REFRESH_RATE_LIMITED; no Redis.</remarks>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-refresh")]
    [ProducesResponseType<ProblemDetails>(429)]
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

    /// <summary>Revokes every refresh-token family and disables push delivery for the current account.</summary>
    [HttpPost("logout-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        var result = await sender.Send(new LogoutAllCommand(User.GetActorId(), User.GetSessionFamilyId()), ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }

    /// <summary>
    /// Lấy thông tin tài khoản của phiên đăng nhập hiện tại.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken ct)
    {
        var account = await sender.Send(new GetCurrentAccountQuery(User.GetActorId()), ct);
        if (account is null) return Unauthorized();
        account = await EnrichAvatarAsync(User.GetActorId(), account, ct);
        Response.Headers.ETag = ProfileEtag.Format(account.ProfileRevision);
        return Ok(account);
    }

    /// <summary>Updates the current account profile. Role, organization, identity provider and password are not mutable here.</summary>
    [HttpPatch("me")]
    [Authorize]
    [ProducesResponseType<AccountResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<ActionResult<AccountResponse>> UpdateMe(
        Fire3D.Application.Authentication.Commands.RegisterUser.UpdateCurrentProfileRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(
            new Fire3D.Application.Authentication.Commands.RegisterUser.UpdateCurrentProfileCommand(User.GetActorId(), ifMatch, request), ct);
        if (!result.IsSuccess) return ResetProblem(result.Error!);
        var account = await EnrichAvatarAsync(User.GetActorId(), result.Value!, ct);
        Response.Headers.ETag = ProfileEtag.Format(account.ProfileRevision);
        return Ok(account);
    }

    /// <summary>
    /// Registers a credential-bound installation and its current FCM token.
    /// </summary>
    [HttpPut("devices")]
    [Authorize]
    [ProducesResponseType<Fire3D.Application.Users.Commands.RegisterDevice.DeviceRegistrationResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> RegisterDevice(
        [FromBody] Fire3D.Application.Users.Commands.RegisterDevice.RegisterDeviceRequest request,
        [FromHeader(Name = "X-Installation-Key")] string? installationKey, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Users.Commands.RegisterDevice.RegisterDeviceCommand(
            User.GetActorId(), User.GetSessionFamilyId(), request.DeviceUuid, installationKey, request.FcmToken, request.DeviceModel, request.OsVersion, request.AppVersion), ct);
        return result.IsSuccess ? Ok(result.Value) : ResetProblem(result.Error!);
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
        new Fire3D.Application.Users.Commands.RegisterDevice.RevokeDeviceCommand(User.GetActorId(), User.GetSessionFamilyId(), deviceUuid, Request.Headers["X-Installation-Key"].FirstOrDefault()), ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }

    private ActionResult<TokenResponse> Respond(AuthResult<TokenResponse> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });

    private IActionResult RespondToRegistrationOtpRequest(AuthResult<bool> result)
    {
        if (!result.IsSuccess && result.Error!.Code == "OTP_RATE_LIMITED")
            Response.Headers.RetryAfter = "3600";
        return result.IsSuccess ? Accepted() : ResetProblem(result.Error!);
    }

    private async Task<AccountResponse> EnrichAvatarAsync(Guid userId, AccountResponse account, CancellationToken ct)
    {
        if (avatars is null) return account;
        var avatar = await avatars.GetAvatarAsync(userId, ct);
        return avatar.IsSuccess ? account with { AvatarUrl = avatar.Value!.Url } : account;
    }

    /// <summary>
    /// Gửi hướng dẫn đặt lại mật khẩu qua email. Không cần Bearer.
    /// </summary>
    /// <remarks>Body: email hợp lệ, tối đa 254 ký tự. Trả 202 cho cả email có và không có tài khoản;
    /// 202 chỉ xác nhận đã nhận yêu cầu, không đảm bảo email đã được gửi.
    /// Chỉ tài khoản local đang hoạt động đủ điều kiện reset; tài khoản chỉ dùng Google không được thêm mật khẩu qua luồng này.</remarks>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
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
    /// <remarks>Không cần Bearer. Body: token (64 ký tự hex), newPassword (6–128 ký tự).
    /// Token hết hạn sau 30 phút, chỉ dùng một lần. 400: token/mật khẩu sai; 204: thành công.
    /// Không gửi mật khẩu/token vào log. Token Firebase oobCode cũ không dùng được.</remarks>
    [HttpPost("reset-password")]
    [AllowAnonymous]
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
    /// Requires Bearer authentication. Body requires currentPassword and newPassword (6–128 characters).
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
            User.GetActorId(), User.GetSessionFamilyId(), request.CurrentPassword, request.NewPassword), ct);
        return result.IsSuccess ? NoContent() : ResetProblem(result.Error!);
    }
    private ObjectResult ResetProblem(AuthError error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Errors is { Count: > 0 }) extensions["errors"] = error.Errors;
        return Problem(statusCode: error.Status, title: error.Message,
            detail: error.Errors is { Count: > 0 } ? "Kiểm tra các trường được liệt kê." : null, extensions: extensions);
    }
}
