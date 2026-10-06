using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fire3D.API.Controllers;

// Scoped to onboarding; execute before ApiController's automatic model-state response.
internal sealed class GoogleOnboardingModelStateAttribute : ActionFilterAttribute
{
    public GoogleOnboardingModelStateAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var errors = context.ModelState.Where(x => x.Value?.Errors.Count > 0 && x.Key != "request")
            .ToDictionary(x => x.Key.StartsWith("$.") ? x.Key[2..] : x.Key.Length == 0 ? "request" : x.Key,
                x => new[] { x.Key == "$.dob" ? "Use a valid date in YYYY-MM-DD format." : "Invalid or unsupported field value." });
        if (errors.Count == 0) errors["request"] = ["A valid JSON request is required."];
        var problem = new ProblemDetails { Status = 400, Title = "One or more fields are invalid." };
        problem.Extensions["code"] = "VALIDATION_ERROR";
        problem.Extensions["errors"] = errors;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(problem) { StatusCode = 400 };
    }
}
