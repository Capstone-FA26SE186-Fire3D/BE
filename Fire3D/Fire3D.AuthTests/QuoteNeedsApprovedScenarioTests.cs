using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Xunit;

namespace Fire3D.AuthTests;

// BE#217 (Flow 1 PC06): a Building is quoted only once one of its situations passed thẩm định.
public sealed class QuoteNeedsApprovedScenarioTests
{
    [BillingPostgresFact]
    public async Task A_building_without_an_approved_situation_cannot_be_quoted()
    {
        await using var database = await BillingDatabase.Create(); await using var db = database.Context(); var service = new BillingService(db);
        var policy = await service.CreateQuotaPolicy(BillingDatabase.Admin, BillingDatabase.Admin, new("tokens", DateTimeOffset.UtcNow.AddDays(-1)), default);
        var package = await service.SavePackage(BillingDatabase.Admin, BillingDatabase.Admin, null, new("V7", "V7", 100, 6, true, null, 25, 1000, policy.Id), null, default);
        await database.Sql($"INSERT INTO billing_test_unapproved_buildings VALUES('{BillingDatabase.Building}')");

        var refused = await Assert.ThrowsAsync<BillingException>(() => service.CreateQuotation(BillingDatabase.Owner, BillingDatabase.Owner,
            new([new(BillingDatabase.Building, package.Id, "New")]), "no-approved", default));
        Assert.Equal((409, "QUOTE_REQUIRES_APPROVED_SCENARIO"), (refused.Status, refused.Code));
        Assert.Equal(BillingDatabase.Building, refused.Extensions!["buildingId"]);
        Assert.Equal(0L, await database.Scalar("SELECT count(*) FROM quotations"));

        // Once a situation of the Building passed thẩm định the quotation is created as before.
        db.ChangeTracker.Clear();
        await database.Sql($"DELETE FROM billing_test_unapproved_buildings WHERE building_id='{BillingDatabase.Building}'");
        var quote = await service.CreateQuotation(BillingDatabase.Owner, BillingDatabase.Owner, new([new(BillingDatabase.Building, package.Id, "New")]), "approved", default);
        Assert.Equal(600, quote.TotalAmount);

        // PlatformAdmin issuing an existing Draft is never blocked, even if the Building has no Approved situation any more (review fix).
        db.ChangeTracker.Clear();
        await database.Sql($"INSERT INTO billing_test_unapproved_buildings VALUES('{BillingDatabase.Building}')");
        var start = new DateTimeOffset(DateTime.UtcNow.Year + 1, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var issued = await service.IssueQuotation(BillingDatabase.Admin, BillingDatabase.Admin, quote.Id,
            new(1400, "Terms", DateTimeOffset.UtcNow.AddDays(1), [new(quote.Items[0].Id, start)]), BillingETag.Format(quote.Id, quote.Revision), default);
        Assert.Equal("Issued", issued.Status);
    }
}

public sealed partial class AuthIntegrationTests
{
    // The real check (migrated database): an Approved review whose hash matches the version counts, whoever decided it.
    [PostgresFact]
    public async Task Building_has_an_approved_situation_only_after_thẩm_định()
    {
        var f = await SeedReadyPackage();
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        Assert.Equal(false, await ScalarAsync($"SELECT building_has_approved_scenario('{building}')"));
        await ApproveRelease(f); // submits the version and approves it (PlatformAdmin decisions before BE#211 keep counting)
        Assert.Equal(true, await ScalarAsync($"SELECT building_has_approved_scenario('{building}')"));
        Assert.Equal(false, await ScalarAsync($"SELECT building_has_approved_scenario('{Guid.NewGuid()}')"));
    }
}
