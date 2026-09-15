using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.CreateOrganization;
using Fire3D.Application.Administration.Commands.SetAccountActive;
using Fire3D.Application.Administration.Commands.SetOrganizationActive;
using Fire3D.Application.Administration.Queries.GetAccount;
using Fire3D.Application.Administration.Queries.GetOrganization;
using Fire3D.Application.Administration.Queries.ListAccounts;
using Fire3D.Application.Administration.Queries.ListOrganizations;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.CreateAccount;
using Fire3D.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Controllers_require_auth_by_default_and_anonymous_management_requests_are_rejected()
    {
        var options = factory!.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var filter = Assert.Single(options.Filters.OfType<AuthorizeFilter>());
        Assert.Contains(filter.Policy!.Requirements, x => x is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        foreach (var path in new[] { "/api/accounts", $"/api/accounts/{adminId}",
            "/api/organizations", $"/api/organizations/{Guid.NewGuid()}", "/api/auth/me" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/organizations",
            new CreateOrganizationRequest("Unauthorized", "unauthorized"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PatchAsJsonAsync($"/api/accounts/{adminId}/status",
            new SetActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PatchAsJsonAsync($"/api/organizations/{Guid.NewGuid()}/status",
            new SetActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        await LoginAsync();
    }

    [PostgresFact]
    public async Task Signed_tokens_cannot_override_database_role_organization_or_session_owner()
    {
        await AuthorizeAdminAsync();
        var account = await CreateAccountAsync("claims@example.test", UserRole.Trainee);
        var token = await LoginAsync(account.Email);
        var original = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        foreach (var changedClaim in new[] { "role", "organization_id", "sub", "sid" })
        {
            var claims = original.Claims.Where(x => x.Type is "sub" or "sid" or "role" or "organization_id")
                .Where(x => x.Type != changedClaim).ToList();
            claims.Add(new Claim(changedClaim, changedClaim switch
            {
                "role" => "PlatformAdmin", "sub" => adminId.ToString(), _ => Guid.NewGuid().ToString()
            }));
            var forged = new JwtSecurityToken("Fire3D.Tests", "Fire3D.Tests.Client", claims,
                DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(signingKey)), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(forged));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
        }
    }

    [PostgresFact]
    public async Task Application_handlers_reject_non_admin_even_without_controller_authorization()
    {
        await AuthorizeAdminAsync();
        var organization = await CreateOrganizationAsync("application-guard");
        var member = await CreateAccountAsync("guard-member@example.test", UserRole.OrganizationUser, organization.Id);
        var trainee = await CreateAccountAsync("guard-trainee@example.test", UserRole.Trainee);
        // Exercise handlers directly so removing an HTTP attribute cannot silently remove the use-case guard.
        foreach (var actor in new[] { member.Id, trainee.Id, Guid.NewGuid() })
        {
            using var scope = factory!.Services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal(403, (await sender.Send(new ListAccountsQuery(actor, new AccountFilter()))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new GetAccountQuery(actor, adminId))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new ListOrganizationsQuery(actor, new OrganizationFilter()))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new GetOrganizationQuery(actor, organization.Id))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new CreateOrganizationCommand(actor,
                new CreateOrganizationRequest("Blocked", "blocked"), Guid.NewGuid()))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new SetAccountActiveCommand(actor, adminId, false, Guid.NewGuid()))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new SetOrganizationActiveCommand(actor, organization.Id, false, Guid.NewGuid()))).Error!.Status);
            Assert.Equal(403, (await sender.Send(new CreateAccountCommand(actor,
                new CreateAccountRequest("escalate@example.test", password, null, UserRole.PlatformAdmin, null)))).Error!.Status);
        }
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM organizations"));
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM users"));
    }
}
