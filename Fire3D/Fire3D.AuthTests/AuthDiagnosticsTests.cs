using System.Diagnostics.Metrics;
using Fire3D.Application.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthDiagnosticsTests
{
    [Fact]
    public void Login_measurements_have_only_bounded_phase_labels()
    {
        var measurements=new List<(double Value,string[] Tags)>();
        using var listener=new MeterListener();
        listener.InstrumentPublished=(instrument,l)=>{if(instrument.Meter.Name==AuthDiagnostics.MeterName)l.EnableMeasurementEvents(instrument);};
        listener.SetMeasurementEventCallback<double>((_,value,tags,_)=>
        {lock(measurements)measurements.Add((value,tags.ToArray().Select(t=>$"{t.Key}={t.Value}").ToArray()));});
        listener.Start();
        using(AuthDiagnostics.Measure(LoginPhase.PasswordVerification)){}
        Assert.Contains(measurements,m=>m.Value>=0 && m.Tags.SequenceEqual(new[]{"phase=PasswordVerification"}));
        Assert.All(measurements,m=>Assert.All(m.Tags,t=>Assert.StartsWith("phase=",t)));
    }
}
