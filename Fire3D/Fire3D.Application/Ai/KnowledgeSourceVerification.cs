namespace Fire3D.Application.Ai;

/// <summary>
/// What an Admin learns before approving a source (BE#188). Outcome is Match, HashMismatch (ActualHash is what the address serves now)
/// or Unavailable (Reason says why). Reason is a code: NO_SOURCE_URI, URI_INVALID, ADDRESS_NOT_PUBLIC, FETCH_FAILED, TIMEOUT,
/// TOO_LARGE, TOO_MANY_REDIRECTS, REDIRECT_INVALID or HTTP_STATUS_nnn. Nothing about the source changes.
/// </summary>
public sealed record KnowledgeSourceVerification(Guid SourceId, string Outcome, string SourceHash, string? ActualHash, long? SizeBytes,
    int? HttpStatus, string? ContentType, string? Reason, DateTime CheckedAt);

public interface IKnowledgeSourceVerifier
{
    /// <summary>Downloads <paramref name="sourceUri"/> the way the AI service does (https, status 200, at most 20 MB, 3 redirects) and compares its SHA-256.</summary>
    Task<KnowledgeSourceVerification> VerifyAsync(Guid sourceId, string? sourceUri, string sourceHash, CancellationToken ct);
}

public static class KnowledgeSourceVerificationRules
{
    public const string Match = "Match";
    public const string HashMismatch = "HashMismatch";
    public const string Unavailable = "Unavailable";
    /// <summary>Same as the AI service (rag-api app/sources/fetch.py), so a source that verifies here is one it can download.</summary>
    public const int MaxRedirects = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
}
