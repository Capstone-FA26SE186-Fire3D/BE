namespace Fire3D.API.OpenApi;

// Documentation metadata only; handlers retain domain-specific error semantics.
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RequiredRequestHeaderAttribute : Attribute;

/// <summary>Documents a header the endpoint accepts but does not require (for example a replay-only Idempotency-Key).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class OptionalRequestHeaderAttribute : Attribute;

/// <summary>Marks a parameter kept only for compatibility; OpenAPI flags it deprecated with the given replacement.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class DeprecatedParameterAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple=true)]
public sealed class ResponseHeaderAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Buffers the request body before model binding so the action can hash the exact bytes it received.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BufferedBodyAttribute : Attribute, Microsoft.AspNetCore.Mvc.Filters.IResourceFilter
{
    public void OnResourceExecuting(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext context) => context.HttpContext.Request.EnableBuffering();
    public void OnResourceExecuted(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutedContext context) { }
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
