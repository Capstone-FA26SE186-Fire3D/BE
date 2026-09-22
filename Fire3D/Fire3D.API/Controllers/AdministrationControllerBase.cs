using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
[EnableRateLimiting("administration")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public abstract class AdministrationControllerBase : ControllerBase
{
    protected Guid ActorId => User.GetActorId();
    protected Guid NewCorrelationId()
    {
        var id = Guid.NewGuid();
        Response.Headers["X-Correlation-ID"] = id.ToString();
        return id;
    }
    protected ActionResult<T> Respond<T>(AuthResult<T> result, int successStatus = 200) =>
        result.IsSuccess ? StatusCode(successStatus, result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
}
