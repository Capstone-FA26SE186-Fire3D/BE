using System.Net;
using System.Text.Json;
using Fire3D.Application.Support;
using Fire3D.Infrastructure.Support;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static readonly string[] SupportTicketStatuses = ["Open", "InProgress", "Resolved", "Closed"];

    private static void AssertTicketCounts(JsonElement page, long open, long inProgress, long resolved, long closed)
    {
        var counts = page.GetProperty("counts");
        Assert.Equal(SupportTicketStatuses.Order().ToArray(), counts.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal([open, inProgress, resolved, closed], SupportTicketStatuses.Select(s => counts.GetProperty(s).GetInt64()).ToArray());
    }

    [PostgresFact]
    public async Task Support_ticket_lists_count_each_status_in_scope_and_ignore_the_status_filter()
    {
        var tenant = await SeedAiTenant(); var other = await SeedAiTenant(); var empty = await SeedAiTenant(); var af = await SeedPlaytestFamily(adminId);
        await WithSupportRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var service = new SupportService(db);
            async Task<Guid> Create(AiTenant who, string key)
            {
                var created = await service.Execute("CreateTicket", who.User, who.Family, null, new CreateTicketRequest("Help " + key, "Please check"), key, null, default);
                Assert.True(created.IsSuccess, created.Error?.Code);
                return created.Value.GetProperty("id").GetGuid();
            }
            async Task<JsonElement> List(string action, Guid actor, Guid family, SupportQuery query)
            {
                var page = await service.Execute(action, actor, family, null, query, null, null, default);
                Assert.True(page.IsSuccess, page.Error?.Code);
                return page.Value;
            }
            var open = await Create(tenant, "t-open"); var working = await Create(tenant, "t-working"); var resolved = await Create(tenant, "t-resolved"); var closed = await Create(tenant, "t-closed");
            var otherOpen = await Create(other, "o-open"); var otherWorking = await Create(other, "o-working");
            await ExecuteAsync($"""
                UPDATE support_tickets SET status='InProgress',priority='High',assigned_to='{adminId}' WHERE id IN('{working}','{otherWorking}');
                UPDATE support_tickets SET status='Resolved',resolved_at=now() WHERE id='{resolved}';
                UPDATE support_tickets SET status='Closed',priority='High',resolved_at=now() WHERE id='{closed}';
                """);

            // Organization user: own tickets only; the status filter narrows items/total but not counts.
            var mine = await List("ListTickets", tenant.User, tenant.Family, new SupportQuery(Status: "Open"));
            Assert.Equal(1, mine.GetProperty("total").GetInt32());
            Assert.Equal(open, mine.GetProperty("items")[0].GetProperty("id").GetGuid());
            AssertTicketCounts(mine, 1, 1, 1, 1);
            AssertTicketCounts(await List("ListTickets", tenant.User, tenant.Family, new SupportQuery(Status: "Closed", OrganizationId: other.Org)), 1, 1, 1, 1);
            AssertTicketCounts(await List("ListTickets", tenant.User, tenant.Family, new SupportQuery(Priority: "High")), 0, 1, 0, 1);
            AssertTicketCounts(await List("ListTickets", tenant.User, tenant.Family, new SupportQuery(AssignedTo: adminId)), 0, 1, 0, 0);
            AssertTicketCounts(await List("ListTickets", other.User, other.Family, new SupportQuery()), 1, 1, 0, 0);
            var none = await List("ListTickets", empty.User, empty.Family, new SupportQuery(Status: "Open"));
            Assert.Equal(0, none.GetProperty("total").GetInt32());
            AssertTicketCounts(none, 0, 0, 0, 0);

            // PlatformAdmin: every organization, or the one in organizationId, with priority/assignedTo still applied.
            var queue = await List("AdminListTickets", adminId, af, new SupportQuery(Status: "InProgress"));
            Assert.Equal(2, queue.GetProperty("total").GetInt32());
            AssertTicketCounts(queue, 2, 2, 1, 1);
            AssertTicketCounts(await List("AdminListTickets", adminId, af, new SupportQuery(Status: "Resolved", OrganizationId: tenant.Org)), 1, 1, 1, 1);
            AssertTicketCounts(await List("AdminListTickets", adminId, af, new SupportQuery(OrganizationId: other.Org)), 1, 1, 0, 0);
            AssertTicketCounts(await List("AdminListTickets", adminId, af, new SupportQuery(Priority: "High", OrganizationId: tenant.Org)), 0, 1, 0, 1);
            AssertTicketCounts(await List("AdminListTickets", adminId, af, new SupportQuery(AssignedTo: adminId)), 0, 2, 0, 0);
            AssertTicketCounts(await List("AdminListTickets", adminId, af, new SupportQuery(OrganizationId: empty.Org)), 0, 0, 0, 0);
            Assert.False((await List("AdminListFeedback", adminId, af, new SupportQuery())).TryGetProperty("counts", out _));
        });
    }

    [PostgresFact]
    public async Task Support_ticket_list_routes_return_counts_and_document_them_in_OpenAPI()
    {
        var owner = await SeedSupportUser(); var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{owner}'"))!;
        using var trainee = factory!.CreateClient(); trainee.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/support/tickets") { Content = System.Net.Http.Json.JsonContent.Create(new { subject = "Counts", description = "Chip counts" }) };
        create.Headers.Add("Idempotency-Key", "counts-http");
        Assert.Equal(HttpStatusCode.Created, (await trainee.SendAsync(create)).StatusCode);
        static async Task<JsonElement> Json(HttpResponseMessage r) { var text = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, text); return JsonDocument.Parse(text).RootElement.Clone(); }
        var mine = await Json(await trainee.GetAsync("/api/support/tickets?status=Closed"));
        Assert.Equal(0, mine.GetProperty("total").GetInt32());
        AssertTicketCounts(mine, 1, 0, 0, 0);
        var queue = await Json(await admin.GetAsync("/api/admin/support/tickets?status=InProgress&pageSize=5"));
        Assert.Equal(0, queue.GetProperty("total").GetInt32());
        Assert.Equal(5, queue.GetProperty("pageSize").GetInt32());
        AssertTicketCounts(queue, 1, 0, 0, 0);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json")); var root = doc.RootElement;
        foreach (var path in new[] { "/api/support/tickets", "/api/admin/support/tickets" })
            Assert.EndsWith("/SupportTicketPage", root.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var page = schemas.GetProperty("SupportTicketPage");
        Assert.Equal(["counts", "items", "page", "pageSize", "total"], page.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.EndsWith("/SupportTicketStatusCounts", page.GetProperty("properties").GetProperty("counts").GetProperty("$ref").GetString());
        var counts = schemas.GetProperty("SupportTicketStatusCounts");
        Assert.Equal(SupportTicketStatuses.Order().ToArray(), counts.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(SupportTicketStatuses.Order().ToArray(), counts.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order().ToArray());
    }
}
