using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Diagnostics;
namespace Fire3D.API.Extensions;
public sealed class PasswordResetExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not PasswordResetException ex) return false;
        context.Response.StatusCode=ex.Error.Status;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext=context,
            ProblemDetails=new() { Status=ex.Error.Status,Title=ex.Error.Message,Extensions={ ["code"]=ex.Error.Code } } });
        return true;
    }
}
