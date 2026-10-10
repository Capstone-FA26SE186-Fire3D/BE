namespace Fire3D.API.OpenApi;

// Documentation metadata only; handlers retain domain-specific error semantics.
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RequiredRequestHeaderAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method, AllowMultiple=true)]
public sealed class ResponseHeaderAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
