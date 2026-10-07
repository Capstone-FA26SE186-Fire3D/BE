using System.Text.Json;
using Fire3D.Application.Authentication;
namespace Fire3D.Application.Ifc;
public sealed class ProcessingWorkerOptions
{
    public bool DispatcherEnabled { get; set; }
    public bool WorkerApiEnabled { get; set; }
    public string Transport { get; set; } = "Http";
    public bool ConsumerEnabled { get; set; }
    public string WorkerUrl { get; set; } = "";
    public string MachineKey { get; set; } = "";
    public string[] AllowedToolchains { get; set; } = [];
    public int PollSeconds { get; set; } = 5;
}
public interface IRevisionProcessingStore
{
    Task<AuthResult<Guid>> RequestAsync(Guid actor, Guid revision, string key, CancellationToken ct);
}
public interface IProcessingWorkerGate
{
    Task<AuthResult<JsonElement>> ExecuteAsync(string action, Guid job, JsonElement input, CancellationToken ct);
}
public interface IProcessingDispatchGate
{
    Task<JsonElement> ExecuteAsync(string action, string? key, Guid? token, Guid? receipt, CancellationToken ct);
}
