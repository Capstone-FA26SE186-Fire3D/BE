using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Organization_creation_validates_slug_and_lists_without_deleted_records()
    {
        await AuthorizeAdminAsync();
        var response = await client.PostAsJsonAsync("/api/organizations", new { name = " Example Org ", slug = " EXAMPLE-ORG " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var organization = (await response.Content.ReadFromJsonAsync<OrganizationResponse>(Json))!;
        Assert.Equal("Example Org", organization.Name);
        Assert.Equal("example-org", organization.Slug);
        var correlation = Guid.Parse(response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{organization.Id}' AND user_id='{adminId}' AND organization_id='{organization.Id}' AND correlation_id='{correlation}' AND action='Create'"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/organizations", new { name = "Duplicate", slug = "example-org" })).StatusCode);
        foreach (var slug in new[] { "", "bad_slug", "-bad", "bad--slug", new string('a', 101) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/organizations", new { name = "Bad", slug })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/organizations", new { name = " ", slug = "valid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/organizations?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/accounts?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/accounts?role=999")).StatusCode);
        var page = await client.GetFromJsonAsync<PageResponse<OrganizationResponse>>("/api/organizations?search=EXAMPLE&isActive=true&pageSize=1", Json);
        Assert.Equal(1, page!.TotalCount);
        Assert.Equal(organization.Id, Assert.Single(page.Items).Id);
        await ExecuteAsync($"UPDATE organizations SET deleted_at=now() WHERE id='{organization.Id}'");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/organizations/{organization.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = true })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PageResponse<OrganizationResponse>>("/api/organizations", Json))!.Items);
    }

    [PostgresFact]
    public async Task Only_admin_can_read_or_change_organizations_and_accounts()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/organizations")).StatusCode);
        await AuthorizeAdminAsync();
        var organization = await CreateOrganizationAsync("scope-one");
        var otherOrganization = await CreateOrganizationAsync("scope-two");
        var member = await CreateAccountAsync("member@example.test", UserRole.OrganizationUser, organization.Id);
        await CreateAccountAsync("trainee@example.test", UserRole.Trainee);
        var page = await client.GetFromJsonAsync<PageResponse<ManagedAccountResponse>>(
            $"/api/accounts?organizationId={organization.Id}&role=OrganizationUser&isActive=true&search=MEMBER", Json);
        Assert.Equal(member.Id, Assert.Single(page!.Items).Id);
        var detail = await client.GetStringAsync($"/api/accounts/{member.Id}");
        Assert.DoesNotContain("password", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", detail, StringComparison.OrdinalIgnoreCase);
        foreach (var email in new[] { "member@example.test", "trainee@example.test" })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);
            foreach (var path in new[] { "/api/accounts", $"/api/accounts/{adminId}", "/api/organizations",
                $"/api/organizations/{organization.Id}", $"/api/organizations/{otherOrganization.Id}" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/organizations", new { name = "Forbidden", slug = "forbidden" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/accounts/{adminId}/status", new { isActive = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = false })).StatusCode);
        }
    }

    [PostgresFact]
    public async Task Account_deactivation_revokes_all_sessions_and_reactivation_requires_new_login()
    {
        var adminToken = await AuthorizeAdminAsync();
        var account = await CreateAccountAsync("target@example.test", UserRole.Trainee);
        var first = await LoginAsync(account.Email);
        var second = await LoginAsync(account.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/accounts/{adminId}/status", new { isActive = false })).StatusCode);
        var locked = await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.False((await locked.Content.ReadFromJsonAsync<ManagedAccountResponse>(Json))!.IsActive);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{account.Id}' AND revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(account.Email, password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{account.Id}' AND action='Update'"));
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { isActive = true })).StatusCode);
        foreach (var token in new[] { first, second })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
        var fresh = await LoginAsync(account.Email);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fresh.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{Guid.NewGuid()}")).StatusCode);
    }

    [PostgresFact]
    public async Task Organization_lock_blocks_provisioning_and_revokes_member_sessions_only()
    {
        var admin = await AuthorizeAdminAsync();
        var organization = await CreateOrganizationAsync("lock-test");
        var account = await CreateAccountAsync("org-lock@example.test", UserRole.OrganizationUser, organization.Id);
        var token = await LoginAsync(account.Email);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("blocked@example.test", password, null, UserRole.OrganizationUser, organization.Id), Json)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = true })).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var fresh = await LoginAsync(account.Email);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fresh.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(true, await ScalarAsync($"SELECT is_active FROM users WHERE id='{account.Id}'"));
        Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{organization.Id}' AND action='Update'"));
    }

    [PostgresFact]
    public async Task Concurrent_admin_deactivation_leaves_one_active_administrator()
    {
        var first = await AuthorizeAdminAsync();
        var secondAccount = await CreateAccountAsync("second-admin@example.test", UserRole.PlatformAdmin);
        var second = await LoginAsync(secondAccount.Email);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{secondAccount.Id}/status");
        firstRequest.Headers.Authorization = new("Bearer", first.AccessToken);
        firstRequest.Content = JsonContent.Create(new { isActive = false });
        using var secondRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{adminId}/status");
        secondRequest.Headers.Authorization = new("Bearer", second.AccessToken);
        secondRequest.Content = JsonContent.Create(new { isActive = false });
        var responses = await Task.WhenAll(client.SendAsync(firstRequest), client.SendAsync(secondRequest));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users WHERE role='PlatformAdmin' AND is_active"));
    }

    [PostgresFact]
    public async Task Audit_failure_rolls_back_status_and_session_revocation()
    {
        await AuthorizeAdminAsync();
        var account = await CreateAccountAsync("rollback@example.test", UserRole.Trainee);
        var token = await LoginAsync(account.Email);
        await ExecuteAsync("""
            CREATE FUNCTION reject_test_update_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.action = 'Update' THEN RAISE EXCEPTION 'Synthetic audit failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER reject_test_update_audit BEFORE INSERT ON audit_logs
            FOR EACH ROW EXECUTE FUNCTION reject_test_update_audit();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { isActive = false })).StatusCode);
        Assert.Equal(true, await ScalarAsync($"SELECT is_active FROM users WHERE id='{account.Id}'"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_refresh_and_organization_lock_cannot_leave_a_usable_session()
    {
        var admin = await AuthorizeAdminAsync();
        var organization = await CreateOrganizationAsync("refresh-race");
        var account = await CreateAccountAsync("refresh-race@example.test", UserRole.OrganizationUser, organization.Id);
        var original = await LoginAsync(account.Email);
        var refresh = client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(original.RefreshToken));
        var deactivate = client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = false });
        await Task.WhenAll(refresh, deactivate);
        Assert.Equal(HttpStatusCode.OK, (await deactivate).StatusCode);
        Assert.Contains((await refresh).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{account.Id}' AND revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/organizations/{organization.Id}/status", new { isActive = true })).StatusCode);
        var access = (await refresh).IsSuccessStatusCode
            ? (await (await refresh).Content.ReadFromJsonAsync<TokenResponse>(Json))!.AccessToken
            : original.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        await ExecuteAsync($"UPDATE users SET deleted_at=now() WHERE id='{account.Id}'");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/accounts/{account.Id}/status", new { isActive = true })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PageResponse<ManagedAccountResponse>>($"/api/accounts?organizationId={organization.Id}", Json))!.Items);
    }

    private async Task<TokenResponse> AuthorizeAdminAsync()
    {
        var token = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        return token;
    }

    private async Task<OrganizationResponse> CreateOrganizationAsync(string slug)
    {
        var response = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest(slug, slug));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(Json))!;
    }

    private async Task<AccountResponse> CreateAccountAsync(string email, UserRole role, Guid? organizationId = null)
    {
        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest(email, password, "Test user", role, organizationId), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<AccountResponse>(Json))!;
        var correlation = Guid.Parse(response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{account.Id}' AND user_id='{adminId}' AND correlation_id='{correlation}' AND action='Create'"));
        if (organizationId.HasValue)
            Assert.Equal(organizationId.Value, await ScalarAsync($"SELECT organization_id FROM audit_logs WHERE correlation_id='{correlation}'"));
        return account;
    }
}
