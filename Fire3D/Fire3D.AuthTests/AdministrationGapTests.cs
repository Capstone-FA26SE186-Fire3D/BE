using System.Net;
using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.SetAccountActive;
using Fire3D.Application.Administration.Commands.SetOrganizationActive;
using Fire3D.Application.Administration.Commands.UpdateMyOrganization;
using Fire3D.Application.Administration.Commands.UpdateOrganization;
using Fire3D.Application.Administration.Queries.GetMyOrganization;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Administration;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AdministrationGapTests
{
    [BillingPostgresFact]
    public async Task Organization_list_orders_by_each_supported_timestamp_direction()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql($"""
            UPDATE organizations SET created_at=now()-interval '2 days', updated_at=now() WHERE id='{BillingDatabase.Org}';
            UPDATE organizations SET created_at=now()-interval '1 day', updated_at=now()-interval '3 days' WHERE id='{BillingDatabase.OtherOrg}';
            """);
        await using var context = db.Context();
        var store = new AdministrationStore(context);
        async Task<Guid[]> Ids(string? sort) => (await store.ListOrganizationsAsync(new(Sort: sort), default)).Items.Select(x => x.Id).ToArray();
        Guid[] orgFirst = [BillingDatabase.Org, BillingDatabase.OtherOrg], otherFirst = [BillingDatabase.OtherOrg, BillingDatabase.Org];
        Assert.Equal(otherFirst, await Ids("updatedAt"));
        Assert.Equal(orgFirst, await Ids("-updatedAt"));
        Assert.Equal(orgFirst, await Ids("createdAt"));
        Assert.Equal(otherFirst, await Ids(null));
    }

    [BillingPostgresFact]
    public async Task Account_list_orders_by_each_supported_field_and_direction()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql($"""
            UPDATE users SET full_name='Charlie', last_login_at=now()-interval '1 day', updated_at=now()-interval '3 days', created_at=now()-interval '3 days' WHERE id='{BillingDatabase.Admin}';
            UPDATE users SET full_name='Alice', last_login_at=now()-interval '3 days', updated_at=now()-interval '1 day', created_at=now()-interval '2 days' WHERE id='{BillingDatabase.Owner}';
            UPDATE users SET full_name='Bob', last_login_at=now()-interval '2 days', updated_at=now()-interval '2 days', created_at=now()-interval '1 day' WHERE id='{BillingDatabase.Other}';
            """);
        await using var context = db.Context();
        var store = new AdministrationStore(context);
        async Task<Guid[]> Ids(string sort) => (await store.ListAccountsAsync(new(Sort: sort), default)).Items.Select(x => x.Id).ToArray();
        var (admin, owner, other) = (BillingDatabase.Admin, BillingDatabase.Owner, BillingDatabase.Other);
        Assert.Equal([owner, other, admin], await Ids("-email"));
        Assert.Equal([owner, other, admin], await Ids("fullName"));
        Assert.Equal([admin, other, owner], await Ids("-fullName"));
        Assert.Equal([owner, other, admin], await Ids("lastLoginAt"));
        Assert.Equal([admin, other, owner], await Ids("-lastLoginAt"));
        Assert.Equal([admin, other, owner], await Ids("updatedAt"));
        Assert.Equal([owner, other, admin], await Ids("-updatedAt"));
        Assert.Equal([admin, owner, other], await Ids("createdAt"));
    }

    [BillingPostgresFact]
    public async Task Organization_profile_get_returns_the_handler_problem_when_the_organization_is_disabled()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
        using var factory = new BillingApiTests.Factory(db);
        using var client = factory.As(BillingDatabase.Owner);
        var response = await client.GetAsync("/api/organizations/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.False(response.Headers.Contains("ETag"));
    }

    private static readonly Guid Actor = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid OrgId = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static User OrgUser() => new() { Id = Actor, Email = "org@example.test", Role = UserRole.OrganizationUser, OrganizationId = OrgId, IsActive = true };
    private static User Admin() => new() { Id = Actor, Email = "admin@example.test", Role = UserRole.PlatformAdmin, IsActive = true };
    private static Organization Org(bool active = true) => new() { Id = OrgId, Name = "Org", Slug = "org", IsActive = active, Metadata = "{}" };

    private static ISender Sender(Func<string, object?[], object?> auth, Func<string, object?[], object?> administration)
    {
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method == "DisposeAsync" ? ValueTask.CompletedTask : Task.CompletedTask);
        var services = new ServiceCollection().AddLogging();
        services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(UpdateMyOrganizationCommand).Assembly));
        services.AddSingleton(ResetProxy.For<IAuthStore>(auth));
        services.AddSingleton(ResetProxy.For<IAdministrationStore>((method, args) =>
            method == "BeginManagementTransactionAsync" ? Task.FromResult(transaction) : administration(method, args)));
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }
    private static object? Accounts(string method, User? user, int activeAdmins = 1) => method switch
    {
        "FindUserAsync" => Task.FromResult(user),
        "OrganizationIsActiveAsync" => Task.FromResult(true),
        "CountActiveAdminsAsync" => Task.FromResult(activeAdmins),
        _ => throw new InvalidOperationException("Unexpected: " + method)
    };
    private static Func<string, object?[], object?> Unexpected => (method, _) => throw new InvalidOperationException("Unexpected: " + method);

    [Fact]
    public async Task Update_my_organization_prechecks_the_request_shape()
    {
        var sender = Sender(Unexpected, Unexpected);
        var missing = await sender.Send(new UpdateMyOrganizationCommand(Actor, "\"1\"", null!));
        Assert.Equal(("VALIDATION_ERROR", 400), (missing.Error?.Code, missing.Error?.Status));
        var empty = await sender.Send(new UpdateMyOrganizationCommand(Actor, "\"1\"", new UpdateOrganizationProfileRequest()));
        Assert.Equal(("VALIDATION_ERROR", 400), (empty.Error?.Code, empty.Error?.Status));
        Assert.Contains("at least one", empty.Error!.Message);
    }

    [Fact]
    public async Task Update_my_organization_requires_an_active_organization_user_and_organization()
    {
        var request = new UpdateOrganizationProfileRequest("New name", "1 Street", "0901234567");
        var admin = await Sender((m, _) => Accounts(m, Admin()), Unexpected).Send(new UpdateMyOrganizationCommand(Actor, "\"1\"", request));
        Assert.Equal(("FORBIDDEN", 403), (admin.Error?.Code, admin.Error?.Status));
        var inactive = await Sender((m, _) => Accounts(m, OrgUser()), (m, _) => m == "FindOrganizationAsync" ? Task.FromResult<Organization?>(Org(false)) : Unexpected(m, []))
            .Send(new UpdateMyOrganizationCommand(Actor, "\"1\"", request));
        Assert.Equal(("UNAUTHORIZED", 401), (inactive.Error?.Code, inactive.Error?.Status));
        var vanished = await Sender((m, _) => Accounts(m, OrgUser()), (m, _) => m switch
        {
            "FindOrganizationAsync" => Task.FromResult<Organization?>(Org()),
            "UpdateOrganizationProfileAsync" => Task.FromResult(OrganizationProfileUpdateResult.Unavailable),
            _ => Unexpected(m, [])
        }).Send(new UpdateMyOrganizationCommand(Actor, "\"1\"", request));
        Assert.Equal(("UNAUTHORIZED", 401), (vanished.Error?.Code, vanished.Error?.Status));
    }

    [Fact]
    public async Task Get_my_organization_requires_an_available_member_account()
    {
        var missing = await Sender((m, _) => Accounts(m, null), Unexpected).Send(new GetMyOrganizationQuery(Actor));
        Assert.Equal(("UNAUTHORIZED", 401), (missing.Error?.Code, missing.Error?.Status));
        var admin = await Sender((m, _) => Accounts(m, Admin()), Unexpected).Send(new GetMyOrganizationQuery(Actor));
        Assert.Equal(("FORBIDDEN", 403), (admin.Error?.Code, admin.Error?.Status));
    }

    [Fact]
    public async Task Platform_admin_commands_reject_non_admins_missing_flags_and_the_last_admin()
    {
        var notAdmin = await Sender((m, _) => Accounts(m, OrgUser()), Unexpected)
            .Send(new UpdateOrganizationCommand(Actor, OrgId, "\"1\"", new UpdateOrganizationProfileRequest("Name", null, null), Guid.NewGuid()));
        Assert.Equal(("FORBIDDEN", 403), (notAdmin.Error?.Code, notAdmin.Error?.Status));
        var noFlag = await Sender((m, _) => Accounts(m, Admin()), Unexpected).Send(new SetOrganizationActiveCommand(Actor, OrgId, null, Guid.NewGuid()));
        Assert.Equal(("VALIDATION_ERROR", 400), (noFlag.Error?.Code, noFlag.Error?.Status));

        var target = new User { Id = Guid.NewGuid(), Email = "other-admin@example.test", Role = UserRole.PlatformAdmin, IsActive = true };
        var last = await Sender((m, args) => m == "FindUserAsync" ? Task.FromResult<User?>((Guid)args[0]! == Actor ? Admin() : target) : Accounts(m, null, 1), Unexpected)
            .Send(new SetAccountActiveCommand(Actor, target.Id, false, Guid.NewGuid()));
        Assert.Equal(("LAST_ADMIN", 409), (last.Error?.Code, last.Error?.Status));
    }
}
