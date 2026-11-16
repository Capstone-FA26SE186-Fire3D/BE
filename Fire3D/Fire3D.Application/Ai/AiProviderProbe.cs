namespace Fire3D.Application.Ai;

/// <summary>What GET {BaseUrl}/ready of the AI service answered (BE#185). Code is OK, AI_PROVIDER_UNREACHABLE (no answer: connection,
/// DNS, TLS or timeout) or AI_PROVIDER_NOT_READY (it answered with HttpStatus other than 200).</summary>
public sealed record AiProbeResult(string Code, int? HttpStatus = null)
{
    public const string Ok = "OK";
    public const string Unreachable = "AI_PROVIDER_UNREACHABLE";
    public const string NotReady = "AI_PROVIDER_NOT_READY";
}

public interface IAiProviderProbe
{
    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}
