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
        // Swagger can send Accept: text/plain. Preserve the error contract even
        // when none of the registered problem writers accepts that media type.
        if(!await problems.TryWriteAsync(new ProblemDetailsContext{HttpContext=context,ProblemDetails=details}))
            await context.Response.WriteAsJsonAsync(details,options:null,
                contentType:"application/problem+json",cancellationToken:ct);
        return true;
    }
}
