using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fire3D.Application.Authentication;

public enum LoginPhase { Total, Lookup, PasswordVerification, LockWait, Revalidation, Persistence }

/// <summary>Bounded phase labels only. Never attach account, email, SQL, credentials or tokens.</summary>
public static class AuthDiagnostics
{
    public const string MeterName="FET3D.Authentication";
    private static readonly Meter Meter=new(MeterName);
    private static readonly Histogram<double> Duration=Meter.CreateHistogram<double>("fet3d.auth.login.phase.duration","ms");
    public static IDisposable Measure(LoginPhase phase) => new Measurement(phase);
    private sealed class Measurement(LoginPhase phase) : IDisposable
    {
        private readonly long start=Stopwatch.GetTimestamp();
        public void Dispose() => Duration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            new KeyValuePair<string,object?>("phase",phase.ToString()));
    }
}
