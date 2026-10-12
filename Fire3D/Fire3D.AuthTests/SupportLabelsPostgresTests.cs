using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Support;
using Fire3D.Infrastructure.Support;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Support_labels_show_creator_author_and_organization_with_email_only_for_admin()
    {
        var tenant = await SeedAiTenant(); var stranger = await SeedAiTenant(); var af = await SeedPlaytestFamily(adminId);
        await ExecuteAsync($"UPDATE users SET full_name='Lan Nguyen' WHERE id='{tenant.User}'; UPDATE users SET full_name='Support Admin' WHERE id='{adminId}'");
        var tenantEmail = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{tenant.User}'"))!;
        var adminEmail = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{adminId}'"))!;
        await WithSupportRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var service = new SupportService(db);
            var feedback = await service.Execute("CreateFeedback", tenant.User, tenant.Family, null, new CreateFeedbackRequest("Bug", "Broken exit sign", 4), "fb", null, default);
            Assert.True(feedback.IsSuccess, feedback.Error?.Code);
            var fid = feedback.Value.GetProperty("id").GetGuid();
            Assert.Equal("Lan Nguyen", feedback.Value.GetProperty("createdBy").GetProperty("displayName").GetString());
            Assert.False(feedback.Value.GetProperty("createdBy").TryGetProperty("email", out _));
            Assert.Equal("AI organization", feedback.Value.GetProperty("organization").GetProperty("name").GetString());
            var ticket = await service.Execute("CreateTicket", tenant.User, tenant.Family, null, new CreateTicketRequest("Help", "Please check", fid), "tk", null, default);
            var id = ticket.Value.GetProperty("id").GetGuid();
            Assert.Equal(tenant.Org, ticket.Value.GetProperty("organization").GetProperty("id").GetGuid());

            // Admin feedback detail by ID, with the creator email; regular users cannot use the admin action.
            var detail = await service.Execute("AdminGetFeedback", adminId, af, fid, new { }, null, null, default);
            Assert.Equal(tenantEmail, detail.Value.GetProperty("createdBy").GetProperty("email").GetString());
            Assert.Equal(1, detail.Value.GetProperty("revision").GetInt64());
            Assert.Equal("FORBIDDEN", (await service.Execute("AdminGetFeedback", tenant.User, tenant.Family, fid, new { }, null, null, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await service.Execute("AdminGetFeedback", adminId, af, Guid.NewGuid(), new { }, null, null, default)).Error?.Code);

            // Admin message: the owner sees the staff name but never the staff email; the admin view shows both emails.
            var adminMessage = await service.Execute("AdminMessage", adminId, af, id, new MessageRequest("We are on it"), "am", null, default);
            Assert.Equal(adminEmail, adminMessage.Value.GetProperty("author").GetProperty("email").GetString());
            Assert.True((await service.Execute("Message", tenant.User, tenant.Family, id, new MessageRequest("Thanks"), "um", null, default)).IsSuccess);
            var ownerView = (await service.Execute("GetTicket", tenant.User, tenant.Family, id, new SupportQuery(), null, null, default)).Value;
            var ownerMessages = ownerView.GetProperty("messages").GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal("Support Admin", ownerMessages[0].GetProperty("author").GetProperty("displayName").GetString());
            Assert.Equal(adminId, ownerMessages[0].GetProperty("authorId").GetGuid());
            Assert.DoesNotContain(adminEmail, ownerView.GetRawText());
            var adminView = (await service.Execute("AdminGetTicket", adminId, af, id, new SupportQuery(), null, null, default)).Value;
            Assert.Equal(tenantEmail, adminView.GetProperty("createdBy").GetProperty("email").GetString());
            Assert.Equal(tenantEmail, adminView.GetProperty("messages").GetProperty("items")[1].GetProperty("author").GetProperty("email").GetString());

            // Replays recompute labels for the caller; receipts never store a label or an email.
            var replay = await service.Execute("CreateTicket", tenant.User, tenant.Family, null, new CreateTicketRequest("Help", "Please check", fid), "tk", null, default);
            Assert.Equal(id, replay.Value.GetProperty("id").GetGuid());
            Assert.Equal("Lan Nguyen", replay.Value.GetProperty("createdBy").GetProperty("displayName").GetString());
            Assert.Equal(adminEmail, (await service.Execute("AdminMessage", adminId, af, id, new MessageRequest("We are on it"), "am", null, default)).Value.GetProperty("author").GetProperty("email").GetString());
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM support_command_receipts WHERE result ? 'createdBy' OR result ? 'author' OR result::text LIKE '%@example.test%'"));

            // Scope is unchanged: another tenant cannot read the ticket.
            Assert.Equal("NOT_FOUND", (await service.Execute("GetTicket", stranger.User, stranger.Family, id, new SupportQuery(), null, null, default)).Error?.Code);
            Assert.Equal(0, (await service.Execute("ListTickets", stranger.User, stranger.Family, null, new SupportQuery(), null, null, default)).Value.GetProperty("total").GetInt32());
            var adminFeedback = (await service.Execute("AdminListFeedback", adminId, af, null, new SupportQuery(OrganizationId: tenant.Org), null, null, default)).Value;
            Assert.Equal(1, adminFeedback.GetProperty("total").GetInt32());
            Assert.Equal(tenantEmail, adminFeedback.GetProperty("items")[0].GetProperty("createdBy").GetProperty("email").GetString());
            Assert.False((await service.Execute("ListFeedback", tenant.User, tenant.Family, null, new SupportQuery(), null, null, default)).Value.GetProperty("items")[0].GetProperty("createdBy").TryGetProperty("email", out _));

            // Deleted creator and organization keep the ticket readable for Admin with IDs and unavailable labels.
            await ExecuteAsync($"UPDATE users SET deleted_at=now() WHERE id='{tenant.User}'; UPDATE organizations SET deleted_at=now() WHERE id='{tenant.Org}'");
            var orphan = (await service.Execute("AdminListTickets", adminId, af, null, new SupportQuery(), null, null, default)).Value.GetProperty("items")[0];
            Assert.Equal(tenant.User, orphan.GetProperty("createdBy").GetProperty("id").GetGuid());
            Assert.False(orphan.GetProperty("createdBy").GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, orphan.GetProperty("createdBy").GetProperty("displayName").ValueKind);
            Assert.False(orphan.GetProperty("createdBy").TryGetProperty("email", out _));
            Assert.Equal(JsonValueKind.Null, orphan.GetProperty("organization").GetProperty("name").ValueKind);
            Assert.True((await service.Execute("AdminGetTicket", adminId, af, id, new SupportQuery(), null, null, default)).IsSuccess);
            // Reads stay reachable for Admin, but nothing is written to a deleted organization's resources.
            Assert.Equal("NOT_FOUND", (await service.Execute("AdminMessage", adminId, af, id, new MessageRequest("Late reply"), "late", null, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await service.Execute("FeedbackStatus", adminId, af, fid, new AdminFeedbackUpdateRequest(Fire3D.Domain.Enums.FeedbackStatus.Reviewed), null, 1, default)).Error?.Code);
        });
    }

    [PostgresFact]
    public async Task Support_admin_feedback_detail_HTTP_returns_etag_for_status_patch()
    {
        var owner = await SeedSupportUser(); var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{owner}'"))!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/feedback") { Content = JsonContent.Create(new { category = "Idea", message = "More maps", rating = 5 }) };
        create.Headers.Add("Idempotency-Key", "fb-http");
        var id = (await (await client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/feedback/{id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var detail = await client.GetAsync($"/api/admin/feedback/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(email, (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("createdBy").GetProperty("email").GetString());
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/feedback/{id}/status") { Content = JsonContent.Create(new { status = "Reviewed" }) };
        patch.Headers.TryAddWithoutValidation("If-Match", detail.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/feedback/{Guid.NewGuid()}")).StatusCode);
    }
}
