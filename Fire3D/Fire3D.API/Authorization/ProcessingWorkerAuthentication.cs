using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace Fire3D.API.Authorization;
public sealed class ProcessingWorkerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> schemes,ILoggerFactory logger,UrlEncoder encoder,IOptions<ProcessingWorkerOptions> worker)
    :AuthenticationHandler<AuthenticationSchemeOptions>(schemes,logger,encoder)
{
    public const string SchemeName="ProcessingWorker";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key=Request.Headers["X-Worker-Key"];
        if(!worker.Value.WorkerApiEnabled || worker.Value.MachineKey.Length<32 || key.Count!=1 || key[0] is not { Length: >= 32 and <= 512 } supplied)
            return Task.FromResult(AuthenticateResult.Fail("Worker proof is required."));
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),SHA256.HashData(Encoding.UTF8.GetBytes(worker.Value.MachineKey))))
            return Task.FromResult(AuthenticateResult.Fail("Worker proof is invalid."));
        var principal=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"processing-worker")],SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,SchemeName)));
    }
}
