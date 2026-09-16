using Fire3D.Application.Authentication.Internal;
using Fire3D.Application.Email;
using Fire3D.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Options;

namespace Fire3D.Application.Authentication.Commands.ForgotPassword;

internal sealed class ForgotPasswordCommandHandler(
    IAuthStore store,
    IEmailService email,
    IOptions<AuthEmailOptions> authOptions,
    TimeProvider clock)
    : IRequestHandler<ForgotPasswordCommand>
{
    public async Task Handle(ForgotPasswordCommand command, CancellationToken ct)
    {
        var normalized = AuthSupport.NormalizeEmail(command.Email);
        if (normalized is null) return; // Chống email enumeration – không báo lỗi

        var user = await store.FindUserByEmailAsync(normalized, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue) return; // Vẫn không báo lỗi

        // Xoá các token cũ chưa dùng để không bị spam
        await store.InvalidateUserResetTokensAsync(user.Id, ct);

        var now = AuthSupport.UtcNow(clock);
        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1)
        };

        await store.SavePasswordResetTokenAsync(token, ct);

        var frontendUrl = authOptions.Value.FrontendUrl.TrimEnd('/');
        var resetLink = $"{frontendUrl}/reset-password?token={token.Id}";

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? "bạn" : user.FullName;
        var html = $"""
            <!DOCTYPE html>
            <html lang="vi">
            <head><meta charset="UTF-8"><title>Đặt lại mật khẩu</title></head>
            <body style="font-family:Arial,sans-serif;background:#f4f4f4;padding:20px">
              <div style="max-width:480px;margin:0 auto;background:#fff;border-radius:8px;padding:32px;box-shadow:0 2px 8px rgba(0,0,0,.08)">
                <h2 style="color:#1a1a2e;margin-top:0">Đặt lại mật khẩu Fire3D</h2>
                <p>Xin chào <strong>{displayName}</strong>,</p>
                <p>Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản <strong>{normalized}</strong>.</p>
                <p>Nhấn nút bên dưới để đặt mật khẩu mới. Link sẽ hết hạn sau <strong>1 giờ</strong>.</p>
                <div style="text-align:center;margin:28px 0">
                  <a href="{resetLink}"
                     style="background:#e94560;color:#fff;padding:12px 32px;border-radius:6px;text-decoration:none;font-weight:bold;font-size:15px">
                    Đặt lại mật khẩu
                  </a>
                </div>
                <p style="font-size:13px;color:#666">Nếu bạn không yêu cầu đặt lại mật khẩu, hãy bỏ qua email này.</p>
                <hr style="border:none;border-top:1px solid #eee;margin:24px 0">
                <p style="font-size:12px;color:#999;text-align:center">© Fire3D Platform</p>
              </div>
            </body>
            </html>
            """;

        await email.SendAsync(normalized, "Đặt lại mật khẩu Fire3D", html, ct);
    }
}
