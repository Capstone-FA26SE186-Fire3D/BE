using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Administration.Commands.ChangeAccountEmail;

/// <summary>BE#235: PlatformAdmin corrects the email of an account that has not proved it yet (first_login_step VerifyEmail).</summary>
public sealed record ChangeAccountEmailRequest(string? Email);
public sealed record ChangeAccountEmailCommand(Guid ActorId, Guid Id, string? Email, string? IfMatch, Guid CorrelationId)
    : IRequest<AuthResult<ManagedAccountResponse>>;
public enum AccountEmailChange { Changed, Stale, NotUnverified, EmailExists }

internal sealed class ChangeAccountEmailCommandHandler(IAdministrationStore store, IAuthStore auth, TimeProvider clock)
    : IRequestHandler<ChangeAccountEmailCommand, AuthResult<ManagedAccountResponse>>
{
    public async Task<AuthResult<ManagedAccountResponse>> Handle(ChangeAccountEmailCommand command, CancellationToken ct)
    {
        if (!ProfileEtag.TryParse(command.IfMatch, out var revision))
            return AuthResult<ManagedAccountResponse>.Fail("PRECONDITION_REQUIRED", "Send the account ETag (GET /api/accounts/{id}) in If-Match.", 428);
        var email = PasswordResetValidation.NormalizeEmail(command.Email);
        if (email is null) return AdministrationSupport.Invalid<ManagedAccountResponse>("Use a valid email address.");
        // The exclusive identity lock serializes with sign-ins, first sign-in verification and status changes.
        await using var transaction = await store.BeginManagementTransactionAsync(ct);
        if (!await AdministrationSupport.IsAdminAsync(auth, command.ActorId, ct)) return AdministrationSupport.Forbidden<ManagedAccountResponse>();
        var target = await auth.FindUserAsync(command.Id, ct);
        if (target is null || target.DeletedAt.HasValue) return AdministrationSupport.NotFound<ManagedAccountResponse>();
        if (target.FirstLoginStep != FirstLoginSteps.VerifyEmail)
            return AuthResult<ManagedAccountResponse>.Fail("EMAIL_ALREADY_VERIFIED", "Only the email of an account that has not verified it can be changed.", 409);
        if (target.ProfileRevision != revision)
            return AuthResult<ManagedAccountResponse>.Fail("PRECONDITION_FAILED", "The account changed since it was read. Reload and try again.", 412);
        if (PasswordResetValidation.NormalizeEmail(target.Email) == email) return AuthResult<ManagedAccountResponse>.Ok(AdministrationSupport.ToResponse(target));
        var now = AuthSupport.UtcNow(clock);
        switch (await store.ChangeUnverifiedEmailAsync(target.Id, email, revision, now, ct))
        {
            case AccountEmailChange.EmailExists: return AuthResult<ManagedAccountResponse>.Fail("EMAIL_EXISTS", "Another account already uses this email.", 409);
            case AccountEmailChange.Stale: return AuthResult<ManagedAccountResponse>.Fail("PRECONDITION_FAILED", "The account changed since it was read. Reload and try again.", 412);
            case AccountEmailChange.NotUnverified: return AuthResult<ManagedAccountResponse>.Fail("EMAIL_ALREADY_VERIFIED", "Only the email of an account that has not verified it can be changed.", 409);
        }
        var admin = (await auth.FindUserAsync(command.ActorId, ct))!;
        await auth.WriteAuditAsync(admin, "Update", target.Id, now, ct, command.CorrelationId);
        target = (await auth.FindUserAsync(command.Id, ct))!;
        await transaction.CommitAsync(ct);
        return AuthResult<ManagedAccountResponse>.Ok(AdministrationSupport.ToResponse(target));
    }
}
