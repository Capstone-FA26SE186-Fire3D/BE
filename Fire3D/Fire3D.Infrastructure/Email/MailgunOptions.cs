namespace Fire3D.Infrastructure.Email;

/// <summary>Cấu hình Mailgun REST API. Bind từ appsettings section "Mailgun".</summary>
public sealed class MailgunOptions
{
    public const string SectionName = "Mailgun";

    /// <summary>Private API key của Mailgun (key-xxx...).</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Sending domain, vd: sandbox3f5edb...mailgun.org hoặc mg.fire3d.com</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Địa chỉ người gửi, vd: no-reply@fire3d.com</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Base URL của Mailgun API (mặc định US region).</summary>
    public string BaseUrl { get; init; } = "https://api.mailgun.net";

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Domain) &&
        !string.IsNullOrWhiteSpace(From);
}
