using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace Fire3D.API.Extensions;
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document,OpenApiDocumentTransformerContext context,CancellationToken ct)
    {
        document.Info.Description="FET3D BE. Runtime authorization is authoritative; API availability does not certify deployment/provider acceptance. See docs/api-implementation-checklist.md.";
        document.Servers=[new OpenApiServer{Url="/"}];
        document.Components??=new OpenApiComponents();document.Components.SecuritySchemes??=new Dictionary<string,IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"]=new OpenApiSecurityScheme{Type=SecuritySchemeType.Http,Scheme="bearer",BearerFormat="JWT",Description="Paste the access token returned by /api/auth/login."};
        foreach(var description in context.DescriptionGroups.SelectMany(group=>group.Items))
        {
            var path="/"+description.RelativePath?.Split('?')[0];
            if(!document.Paths.TryGetValue(path,out var item)||item.Operations is null)continue;
            var operation=item.Operations.SingleOrDefault(pair=>string.Equals(pair.Key.ToString(),description.HttpMethod,StringComparison.OrdinalIgnoreCase)).Value;
            if(operation is null)continue;
            var metadata=description.ActionDescriptor.EndpointMetadata;
            if(metadata.OfType<IAllowAnonymous>().Any()){operation.Security=[];continue;}
            // Controllers are protected by the global authorize filter; role/policy metadata refines it.
            operation.Security=[new OpenApiSecurityRequirement{[new OpenApiSecuritySchemeReference("Bearer",document)]=[]}];
            var requirements=metadata.OfType<IAuthorizeData>().Select(auth=>!string.IsNullOrEmpty(auth.Roles)?"Roles: "+auth.Roles:!string.IsNullOrEmpty(auth.Policy)?"Policy: "+auth.Policy:null).Where(value=>value is not null).Distinct();
            var roleDescription=string.Join("; ",requirements);
            if(roleDescription.Length>0)operation.Description=(operation.Description??"")+" Authorization: "+roleDescription+". Server rechecks actor and tenant.";
        }
        return Task.CompletedTask;
    }
}