using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>BE#188: verifying a knowledge source address against its registered hash. No database, no internet.</summary>
public sealed class KnowledgeSourceVerifierTests
{
    private static readonly Guid SourceId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private const string Uri = "https://docs.example.test/qcvn.pdf";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Script(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler, IHttpClientFactory
    {
        private int next;
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(steps[Math.Min(next++, steps.Length - 1)](request));
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string body, string type = "application/pdf") =>
        _ => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode code) => _ => new(code);
    private static Func<HttpRequestMessage, HttpResponseMessage> Redirect(string? location, HttpStatusCode code = HttpStatusCode.Found) =>
        _ => { var r = new HttpResponseMessage(code); if (location is not null) r.Headers.Location = new System.Uri(location, UriKind.RelativeOrAbsolute); return r; };

    private static Task<KnowledgeSourceVerification> Verify(Script script, string? uri, string hash) =>
        new KnowledgeSourceVerifier(script).VerifyAsync(SourceId, uri, hash, CancellationToken.None);

    [Fact]
    public async Task A_matching_document_is_a_Match_with_what_the_ai_service_would_see()
    {
        var script = new Script(Ok("hello"));
        var result = await Verify(script, Uri, Sha("hello"));
        Assert.Equal("Match", result.Outcome);
        Assert.Equal(Sha("hello"), result.ActualHash);
        Assert.Equal(5, result.SizeBytes);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Null(result.Reason);
        Assert.Equal(SourceId, result.SourceId);
        var request = Assert.Single(script.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("FET3D-source-verify", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task The_registered_hash_is_compared_without_regard_to_case()
    {
        var result = await Verify(new Script(Ok("hello")), Uri, Sha("hello").ToUpperInvariant());
        Assert.Equal("Match", result.Outcome);
    }

    [Fact]
    public async Task A_different_document_is_a_HashMismatch_that_shows_the_hash_it_serves_now()
    {
        var result = await Verify(new Script(Ok("changed page")), Uri, Sha("hello"));
        Assert.Equal("HashMismatch", result.Outcome);
        Assert.Equal(Sha("changed page"), result.ActualHash);
        Assert.Equal(Sha("hello"), result.SourceHash);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_source_without_an_address_is_unavailable_without_any_request(string? uri)
    {
        var script = new Script(Ok("x"));
        var result = await Verify(script, uri, Sha("x"));
        Assert.Equal("Unavailable", result.Outcome);
        Assert.Equal("NO_SOURCE_URI", result.Reason);
        Assert.Empty(script.Requests);
    }

    [Theory]
    [InlineData("http://docs.example.test/a.pdf")]
    [InlineData("https://user:secret@docs.example.test/a.pdf")]
    [InlineData("ftp://docs.example.test/a.pdf")]
    [InlineData("/relative/a.pdf")]
    [InlineData("not a url")]
    public async Task Only_an_https_address_without_credentials_is_fetched(string uri)
    {
        var script = new Script(Ok("x"));
        var result = await Verify(script, uri, Sha("x"));
        Assert.Equal("URI_INVALID", result.Reason);
        Assert.Empty(script.Requests);
    }

    [Fact]
    public async Task A_redirect_to_https_is_followed_and_a_relative_location_is_resolved_against_the_current_address()
    {
        var script = new Script(Redirect("https://cdn.example.test/files/a.pdf", HttpStatusCode.MovedPermanently), Redirect("/files/b.pdf", HttpStatusCode.TemporaryRedirect), Ok("hello"));
        var result = await Verify(script, Uri, Sha("hello"));
        Assert.Equal("Match", result.Outcome);
        Assert.Equal(["https://docs.example.test/qcvn.pdf", "https://cdn.example.test/files/a.pdf", "https://cdn.example.test/files/b.pdf"],
            script.Requests.Select(r => r.RequestUri!.AbsoluteUri).ToArray());
    }

    [Fact]
    public async Task A_redirect_to_http_or_without_a_location_is_refused()
    {
        Assert.Equal("REDIRECT_INVALID", (await Verify(new Script(Redirect("http://docs.example.test/a.pdf")), Uri, Sha("x"))).Reason);
        Assert.Equal("REDIRECT_INVALID", (await Verify(new Script(Redirect("https://u:p@docs.example.test/a.pdf")), Uri, Sha("x"))).Reason);
        var noLocation = await Verify(new Script(Redirect(null)), Uri, Sha("x"));
        Assert.Equal("REDIRECT_INVALID", noLocation.Reason);
        Assert.Equal(302, noLocation.HttpStatus);
    }

    [Fact]
    public async Task Three_redirects_are_allowed_a_fourth_is_not_like_the_ai_service()
    {
        var hop = Redirect("https://docs.example.test/next");
        var allowed = await Verify(new Script(hop, hop, hop, Ok("hello")), Uri, Sha("hello"));
        Assert.Equal("Match", allowed.Outcome);
        var refused = await Verify(new Script(hop), Uri, Sha("hello"));
        Assert.Equal("Unavailable", refused.Outcome);
        Assert.Equal("TOO_MANY_REDIRECTS", refused.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NoContent)]       // the AI service accepts exactly 200
    [InlineData(HttpStatusCode.PartialContent)]
    public async Task Any_status_other_than_200_is_unavailable_and_names_the_status(HttpStatusCode code)
    {
        var result = await Verify(new Script(Status(code)), Uri, Sha("x"));
        Assert.Equal("Unavailable", result.Outcome);
        Assert.Equal("HTTP_STATUS_" + (int)code, result.Reason);
        Assert.Equal((int)code, result.HttpStatus);
        Assert.Null(result.ActualHash);
    }

    [Fact]
    public async Task A_declared_size_over_the_limit_is_refused_before_reading()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentLength = KnowledgeSourceFileRules.MaxBytes + 1;
        var result = await Verify(new Script(_ => response), Uri, Sha("x"));
        Assert.Equal("TOO_LARGE", result.Reason);
    }

    [Fact]
    public async Task The_limit_also_applies_to_a_body_without_a_declared_size_and_exactly_the_limit_is_allowed()
    {
        var over = await Verify(new Script(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Zeros(KnowledgeSourceFileRules.MaxBytes + 1)) }), Uri, Sha("x"));
        Assert.Equal("TOO_LARGE", over.Reason);
        var exact = await Verify(new Script(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Zeros(KnowledgeSourceFileRules.MaxBytes)) }), Uri, Sha("x"));
        Assert.Equal("HashMismatch", exact.Outcome);
        Assert.Equal(KnowledgeSourceFileRules.MaxBytes, exact.SizeBytes);
    }

    [Fact]
    public async Task Network_failures_are_reported_as_codes_not_as_errors()
    {
        Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex) => _ => throw ex;
        Assert.Equal("FETCH_FAILED", (await Verify(new Script(Throw(new HttpRequestException("boom"))), Uri, Sha("x"))).Reason);
        Assert.Equal("ADDRESS_NOT_PUBLIC", (await Verify(new Script(Throw(new HttpRequestException("x", new BlockedAddressException("internal")))), Uri, Sha("x"))).Reason);
        Assert.Equal("TIMEOUT", (await Verify(new Script(Throw(new TaskCanceledException())), Uri, Sha("x"))).Reason);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var script = new Script(_ => throw new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KnowledgeSourceVerifier(script).VerifyAsync(SourceId, Uri, Sha("x"), cts.Token));
    }

    private sealed class Zeros(long length) : Stream
    {
        private long left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, left);
            Array.Clear(buffer, offset, n);
            left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The outbound connection of the verifier only reaches public addresses, whatever the URL or a redirect says.</summary>
public sealed class PublicAddressGuardTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("192.0.1.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    [InlineData("223.255.255.255")]
    public void Public_addresses_are_allowed(string text) => Assert.True(PublicAddressGuard.IsPublic(IPAddress.Parse(text)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]   // cloud instance metadata
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.0")]        // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:0a00:0001::1")] // 6to4 wrapping 10.0.0.1
    [InlineData("64:ff9b::a00:1")]    // NAT64 wrapping 10.0.0.1
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    public void Internal_and_reserved_addresses_are_refused(string text) => Assert.False(PublicAddressGuard.IsPublic(IPAddress.Parse(text)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("169.254.169.254")]
    [InlineData("10.1.2.3")]
    [InlineData("::1")]
    public async Task Connecting_to_an_internal_host_fails_before_any_socket_is_opened(string host)
    {
        await Assert.ThrowsAsync<BlockedAddressException>(async () => await PublicAddressGuard.ConnectAsync(new DnsEndPoint(host, 443), CancellationToken.None));
    }

    [Theory]
    [InlineData("https://127.0.0.1/a.pdf")]
    [InlineData("https://localhost/a.pdf")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/a.pdf")]
    [InlineData("https://10.0.0.5:8443/internal")]
    public async Task The_verifier_with_its_real_handler_reports_an_internal_address_as_not_public(string uri)
    {
        var verifier = new KnowledgeSourceVerifier(new RealHandlerFactory());
        var result = await verifier.VerifyAsync(Guid.NewGuid(), uri, new string('a', 64), CancellationToken.None);
        Assert.Equal("Unavailable", result.Outcome);
        Assert.Equal("ADDRESS_NOT_PUBLIC", result.Reason);
    }

    private sealed class RealHandlerFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(KnowledgeSourceVerifier.CreateHandler());
    }
}
