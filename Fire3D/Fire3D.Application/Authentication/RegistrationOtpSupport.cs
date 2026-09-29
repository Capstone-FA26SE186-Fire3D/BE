using MediatR;

namespace Fire3D.Application.Authentication;

/// <summary>
/// Owns the proof that an anonymous caller controls an email address before
/// self-registration is allowed to create an identity.
/// </summary>
public interface IRegistrationOtpService
{
    Task<AuthResult<bool>> RequestOtpAsync(string email, string? remoteAddress, CancellationToken ct);
    Task<AuthResult<RegistrationOtpVerificationResponse>> VerifyOtpAsync(string email, string otp, CancellationToken ct);
    Task<AuthResult<bool>> ConsumeRegistrationTokenAsync(string email, string registrationToken, CancellationToken ct);
}

public sealed record RegistrationOtpVerificationResponse(string RegistrationToken, DateTime ExpiresAt);

public sealed record RequestRegistrationOtpRequest(string Email);

public sealed record RegistrationOtpEmailJob(Guid Id, string Email, string Otp, Guid LeaseToken, int Attempt);

/// <summary>Durable delivery queue for registration OTPs. It is intentionally separate from legacy account-link verification.</summary>
public interface IRegistrationOtpDeliveryQueue
{
    Task<RegistrationOtpEmailJob?> ClaimAsync(CancellationToken ct);
    Task CompleteAsync(RegistrationOtpEmailJob job, CancellationToken ct);
    Task FailAsync(RegistrationOtpEmailJob job, bool permanent, CancellationToken ct);
}

public sealed record RequestRegistrationOtpCommand(string Email, string? RemoteAddress)
    : IRequest<AuthResult<bool>>;

public sealed class RequestRegistrationOtpCommandHandler(IRegistrationOtpService service)
    : IRequestHandler<RequestRegistrationOtpCommand, AuthResult<bool>>
{
    public Task<AuthResult<bool>> Handle(RequestRegistrationOtpCommand request, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(request.Email);
        return email is null
            ? Task.FromResult(AuthResult<bool>.Fail("INVALID_EMAIL", "Use a valid email address (maximum 254 characters).", 400))
            : service.RequestOtpAsync(email, request.RemoteAddress, ct);
    }
}

public sealed record VerifyRegistrationOtpCommand(string Email, string Otp)
    : IRequest<AuthResult<RegistrationOtpVerificationResponse>>;

public sealed class VerifyRegistrationOtpCommandHandler(IRegistrationOtpService service)
    : IRequestHandler<VerifyRegistrationOtpCommand, AuthResult<RegistrationOtpVerificationResponse>>
{
    public Task<AuthResult<RegistrationOtpVerificationResponse>> Handle(VerifyRegistrationOtpCommand request, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(request.Email);
        if (email is null)
            return Task.FromResult(AuthResult<RegistrationOtpVerificationResponse>.Fail(
                "INVALID_EMAIL", "Use a valid email address (maximum 254 characters).", 400));
        if (string.IsNullOrWhiteSpace(request.Otp) || request.Otp.Length != 6 || request.Otp.Any(c => c is < '0' or > '9'))
            return Task.FromResult(AuthResult<RegistrationOtpVerificationResponse>.Fail(
                "INVALID_OTP", "Enter the six-digit verification code.", 400));
        return service.VerifyOtpAsync(email, request.Otp, ct);
    }
}
