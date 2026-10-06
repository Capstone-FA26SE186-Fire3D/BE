using Fire3D.Application.Authentication;
namespace Fire3D.Application.Scenarios;
public sealed record PackageBuildRequest(string Kind,string BuildTarget);
public interface IScenarioPackageBuildStore
{
    Task<AuthResult<Guid>> BuildAsync(Guid actor,Guid version,PackageBuildRequest request,string? key,CancellationToken ct);
}
