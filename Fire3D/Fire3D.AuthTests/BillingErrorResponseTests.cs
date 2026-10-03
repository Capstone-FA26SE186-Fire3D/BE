using System.Net;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingErrorResponseTests
{
    [Theory]
    [InlineData("text/plain", 503)]
    [InlineData("application/json", 503)]
    [InlineData("application/problem+json", 503)]
    [InlineData("text/html", 503)]
    [InlineData("text/plain", 400)]
    [InlineData("application/json", 400)]
    public async Task Billing_errors_preserve_status_and_safe_contract_for_client_accept_header(string accept, int status)
    {
        var code = status == 503 ? "PAYOS_DISABLED" : "PAYOS_QUOTATION_REQUIRED";
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddProblemDetails();
                services.AddExceptionHandler<BillingExceptionHandler>();
            })
            .Configure(app =>
            {
                app.UseExceptionHandler();
                app.Run(_ => throw new BillingException(status, code, "Safe billing error.",
                    status == 400 ? new() { ["quotationId"] = ["Use the Accepted quotation ID."] } : null));
            })).StartAsync();
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/payos/create");
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("BillingException", text);
        if (status == 400)
            Assert.Equal("Use the Accepted quotation ID.", body.RootElement.GetProperty("errors").GetProperty("quotationId")[0].GetString());
    }
}
