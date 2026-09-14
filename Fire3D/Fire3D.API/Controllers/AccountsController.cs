using System.Security.Claims;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.CreateAccount;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/accounts")]
[Authorize(Roles = "PlatformAdmin")]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountsController(ISender sender) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateAccountCommand(Guid.Parse(User.FindFirstValue("sub")!), request), ct);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
