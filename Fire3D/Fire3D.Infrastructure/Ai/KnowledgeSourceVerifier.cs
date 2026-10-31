using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Fire3D.Application.Ai;

namespace Fire3D.Infrastructure.Ai;

/// <summary>
/// Downloads a registered source address and compares its SHA-256 with the registered hash (BE#188), with the same limits the AI
/// service applies: https only, no credentials, status 200, at most 20 MB and 3 redirects. Each redirect is checked again, and the connection
/// itself only goes to public addresses (<see cref="PublicAddressGuard"/>).
/// </summary>
public sealed class KnowledgeSourceVerifier(IHttpClientFactory clients) : IKnowledgeSourceVerifier
{
    public const string ClientName = "knowledge-source-verify";

    public async Task<KnowledgeSourceVerification> VerifyAsync(Guid sourceId, string? sourceUri, string sourceHash, CancellationToken ct)
    {
        KnowledgeSourceVerification Result(string outcome, string? actual = null, long? size = null, int? status = null, string? type = null, string? reason = null) =>
            new(sourceId, outcome, sourceHash, actual, size, status, type, reason, DateTime.UtcNow);
        KnowledgeSourceVerification Unavailable(string reason, int? status = null) => Result(KnowledgeSourceVerificationRules.Unavailable, status: status, reason: reason);

        if (string.IsNullOrWhiteSpace(sourceUri)) return Unavailable("NO_SOURCE_URI");
        if (!Allowed(sourceUri, out var current)) return Unavailable("URI_INVALID");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(KnowledgeSourceVerificationRules.Timeout);
        var http = clients.CreateClient(ClientName);
        try
        {
            for (var hop = 0; hop <= KnowledgeSourceVerificationRules.MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FET3D-source-verify", "1"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                var code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null) return Unavailable("REDIRECT_INVALID", code);
                    var next = location.IsAbsoluteUri ? location : new Uri(current!, location);
                    if (!Allowed(next.AbsoluteUri, out current)) return Unavailable("REDIRECT_INVALID", code);
                    continue;
                }
                if (code != 200) return Unavailable("HTTP_STATUS_" + code, code); // the AI service accepts exactly 200
                var type = response.Content.Headers.ContentType?.MediaType;
                if (response.Content.Headers.ContentLength > KnowledgeSourceFileRules.MaxBytes) return Unavailable("TOO_LARGE", code);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long size = 0;
                await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, limit.Token)) > 0)
                {
                    size += read;
                    if (size > KnowledgeSourceFileRules.MaxBytes) return Unavailable("TOO_LARGE", code);
                    hash.AppendData(buffer, 0, read);
                }
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                return Result(string.Equals(actual, sourceHash, StringComparison.OrdinalIgnoreCase)
                    ? KnowledgeSourceVerificationRules.Match : KnowledgeSourceVerificationRules.HashMismatch, actual, size, code, type);
            }
            return Unavailable("TOO_MANY_REDIRECTS");
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException) { return Unavailable("ADDRESS_NOT_PUBLIC"); }
        catch (HttpRequestException) { return Unavailable("FETCH_FAILED"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable("TIMEOUT"); }
    }

    private static bool Allowed(string text, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The primary handler of the named client: no automatic redirects, public addresses only.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = PublicAddressGuard.ConnectAsync,
        UseProxy = false
    };
}
