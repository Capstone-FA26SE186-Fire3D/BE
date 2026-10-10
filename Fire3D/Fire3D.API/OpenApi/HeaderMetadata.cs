namespace Fire3D.API.OpenApi;

// Documentation metadata only; handlers retain domain-specific error semantics.
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RequiredRequestHeaderAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method, AllowMultiple=true)]
public sealed class ResponseHeaderAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Reports an unparseable JSON body as 400 EDITOR_JSON_MALFORMED before the generic model-state response,
/// so editor clients can distinguish malformed JSON from schema violations (422 EDITOR_SCHEMA_INVALID).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EditorJsonBodyAttribute : Attribute, Microsoft.AspNetCore.Mvc.Filters.IActionFilter, Microsoft.AspNetCore.Mvc.Filters.IOrderedFilter
{
    public int Order => -3000;
    public void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 400, Title = "Request body must be well-formed JSON." };
        problem.Extensions["code"] = "EDITOR_JSON_MALFORMED";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.Result = new Microsoft.AspNetCore.Mvc.ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
    }
    public void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context) { }
}
