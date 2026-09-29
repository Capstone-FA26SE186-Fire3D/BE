using System.Net;
using Fire3D.Infrastructure.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class MailgunEmailServiceTests
{
    [Fact]
    public async Task Accepted_response_completes_without_exposing_the_message_body()
    {
        var service = Create(HttpStatusCode.OK);

        await service.SendAsync("recipient@example.test", "Subject", "<p>body</p>");
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public async Task Provider_failure_has_safe_status_and_retry_classification(int statusCode, bool permanent)
    {
        var service = Create((HttpStatusCode)statusCode);

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            service.SendAsync("recipient@example.test", "Subject", "<p>body</p>"));

        Assert.Equal((HttpStatusCode)statusCode, exception.StatusCode);
        Assert.Equal(permanent, exception.IsPermanent);
        Assert.DoesNotContain("recipient@example.test", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.mailgun.net", true)]
    [InlineData("http://api.mailgun.net", false)]
    [InlineData("https://api.mailgun.net/?bad=1", false)]
    public void Mailgun_options_require_a_clean_https_api_origin(string baseUrl, bool expected)
    {
        var options = new MailgunOptions { ApiKey = "test-key", Domain = "mg.example.test", From = "FET3D <no-reply@mg.example.test>", BaseUrl = baseUrl };

        Assert.Equal(expected, options.IsValid());
    }

    [Fact]
    public async Task Transport_timeout_is_not_misclassified_as_a_permanent_provider_rejection()
    {
        var service = new MailgunEmailService(new HttpClient(new TimeoutHandler()), Options.Create(new MailgunOptions
        {
            ApiKey = "test-key", Domain = "mg.example.test", From = "FET3D <no-reply@mg.example.test>", BaseUrl = "https://api.mailgun.net"
        }));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.SendAsync("recipient@example.test", "Subject", "<p>body</p>"));
    }

    private static MailgunEmailService Create(HttpStatusCode status) => new(
        new HttpClient(new RespondingHandler(status)),
        Options.Create(new MailgunOptions
        {
            ApiKey = "test-key", Domain = "mg.example.test", From = "FET3D <no-reply@mg.example.test>", BaseUrl = "https://api.mailgun.net"
        }));

    private sealed class RespondingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Add("X-Request-Id", "provider-request-id");
            return Task.FromResult(response);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated timeout."));
    }
}
