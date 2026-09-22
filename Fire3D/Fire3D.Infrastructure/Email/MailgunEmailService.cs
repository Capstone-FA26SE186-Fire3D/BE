using System.Net.Http.Headers;
using System.Text;
using Fire3D.Application.Email;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Email;

/// <summary>
/// Gửi email qua Mailgun REST API (POST /{domain}/messages).
/// Đăng ký bằng AddHttpClient&lt;IEmailService, MailgunEmailService&gt;().
/// </summary>
public sealed class MailgunEmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly MailgunOptions _options;

    public MailgunEmailService(HttpClient http, IOptions<MailgunOptions> options)
    {
        _options = options.Value;
        _http = http;

        // Base address: https://api.mailgun.net/v3/{domain}/
        _http.BaseAddress = new Uri($"{_options.BaseUrl.TrimEnd('/')}/v3/{_options.Domain}/");

        // Basic auth: user = "api", password = ApiKey
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{_options.ApiKey}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("from", _options.From),
            new KeyValuePair<string, string>("to", to),
            new KeyValuePair<string, string>("subject", subject),
            new KeyValuePair<string, string>("html", htmlBody),
        ]);

        using var response = await _http.PostAsync("messages", form, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Email delivery provider rejected the request.", null, response.StatusCode);
        }
    }
}