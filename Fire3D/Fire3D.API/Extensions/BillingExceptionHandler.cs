using System.Diagnostics;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Diagnostics;

namespace Fire3D.API.Extensions;

public sealed class BillingExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context,Exception exception,CancellationToken ct)
    {
        if(exception is not BillingException error)return false;
        context.Response.StatusCode=error.Status;
        var details=new Microsoft.AspNetCore.Mvc.ProblemDetails{Status=error.Status,Title=error.Message};
        details.Extensions["code"]=error.Code;details.Extensions["traceId"]=Activity.Current?.Id??context.TraceIdentifier;
        if(error.Errors is not null)details.Extensions["errors"]=error.Errors;
        await problems.WriteAsync(new ProblemDetailsContext{HttpContext=context,ProblemDetails=details});return true;
    }
}
