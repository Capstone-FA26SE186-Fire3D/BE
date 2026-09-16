namespace Fire3D.Application.Email;

/// <summary>
/// Abstraction cho email service – được implement bởi MailgunEmailService ở Infrastructure layer.
/// </summary>
public interface IEmailService
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
