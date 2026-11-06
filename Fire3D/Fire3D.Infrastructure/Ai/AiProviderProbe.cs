using Fire3D.Application.Ai;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Ai;

/// <summary>
/// Asks the AI service whether it is ready (BE#185): /health/ready used to say OK from the settings alone while every question
/// failed. One call at most every <see cref="CacheFor"/>, so readiness probes do not load the AI service; a short timeout keeps
/// /health/ready fast when the service hangs.
/// </summary>
public sealed class FastApiProviderProbe(IHttpClientFactory clients, IOptions<OrganizationAiOptions> options, TimeProvider? clock = null) : IAiProviderProbe
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (AiProbeResult Result, DateTimeOffset At)? last;

    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (Fresh() is { } cached) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } again) return again;
            var result = await Call(ct);
            last = (result, time.GetUtcNow());
            return result;
        }
        finally { gate.Release(); }
    }

    private AiProbeResult? Fresh() => last is { } l && time.GetUtcNow() - l.At < CacheFor ? l.Result : null;

    private async Task<AiProbeResult> Call(CancellationToken ct)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(o.BaseUrl!.TrimEnd('/') + "/"), "ready"));
        if (!string.IsNullOrEmpty(o.ApiKey)) request.Headers.Authorization = new("Bearer", o.ApiKey);
        try
        {
            using var response = await clients.CreateClient(FastApiAiClient.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (int)response.StatusCode == 200 ? new(AiProbeResult.Ok) : new(AiProbeResult.NotReady, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(AiProbeResult.Unreachable); }
        catch (HttpRequestException) { return new(AiProbeResult.Unreachable); }
    }
}
