using Fire3D.Application.Reporting;
using Fire3D.Application.Support;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Reporting;
using Fire3D.Infrastructure.Support;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Support_audit_records_before_after_reason_and_readable_labels()
    {
        var tenant = await SeedAiTenant(); var af = await SeedPlaytestFamily(adminId);
        await ExecuteAsync($"UPDATE users SET full_name='Support Admin' WHERE id='{adminId}'");
        await WithSupportRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var service = new SupportService(db);
            var id = (await service.Execute("CreateTicket", tenant.User, tenant.Family, null, new CreateTicketRequest("Exit", "Door locked"), "t", null, default)).Value.GetProperty("id").GetGuid();
            var fid = (await service.Execute("CreateFeedback", tenant.User, tenant.Family, null, new CreateFeedbackRequest("Bug", "Map", 3), "f", null, default)).Value.GetProperty("id").GetGuid();

            Assert.Equal("VALIDATION_ERROR", (await service.Execute("TicketStatus", adminId, af, id, new AdminTicketUpdateRequest(SupportTicketStatus.InProgress, SupportPriority.High, adminId, "   "), null, 1, default)).Error?.Code);
            Assert.Equal("VALIDATION_ERROR", (await service.Execute("TicketStatus", adminId, af, id, new AdminTicketUpdateRequest(SupportTicketStatus.InProgress, SupportPriority.High, adminId, new string('r', 1001)), null, 1, default)).Error?.Code);
            // The gate enforces the same rule when called directly.
            Assert.Equal("VALIDATION_ERROR", (await JsonCommandGate.Execute(db, "support_command_gate", "TicketStatus", adminId, af, id, new { status = "InProgress", priority = "High", reason = "  " }, null, 1, default)).Error?.Code);

            // Audit failure rolls the status change back.
            await ExecuteAsync($"ALTER TABLE audit_logs ADD CONSTRAINT support_reason_fault CHECK(target_id<>'{id}'::uuid OR old_values IS NULL) NOT VALID");
            await Assert.ThrowsAsync<PostgresException>(() => service.Execute("TicketStatus", adminId, af, id, new AdminTicketUpdateRequest(SupportTicketStatus.InProgress, SupportPriority.High, adminId, "Checking logs"), null, 1, default));
            Assert.Equal(1L, await ScalarAsync($"SELECT revision FROM support_tickets WHERE id='{id}'"));
            await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT support_reason_fault");

            Assert.True((await service.Execute("TicketStatus", adminId, af, id, new AdminTicketUpdateRequest(SupportTicketStatus.InProgress, SupportPriority.High, adminId, " Checking logs "), null, 1, default)).IsSuccess);
            Assert.True((await service.Execute("FeedbackStatus", adminId, af, fid, new AdminFeedbackUpdateRequest(FeedbackStatus.Reviewed, "Duplicate of map bug"), null, 1, default)).IsSuccess);
            Assert.True((await service.Execute("Message", tenant.User, tenant.Family, id, new MessageRequest("My bank account is 9876-SECRET"), "m", null, default)).IsSuccess);
        });

        await using var reader = BuildingContext(testConnection);
        var audits = new AuditQueries(reader);
        var ticketAudit = (Guid)(await ScalarAsync($"SELECT id FROM audit_logs WHERE target_id='{(await ScalarAsync("SELECT id FROM support_tickets"))}' AND new_values->>'operation'='TicketStatus'"))!;
        var detail = (await audits.Get(adminId, af, ticketAudit, default)).Value!;
        Assert.Equal("Checking logs", detail.Reason);
        Assert.Equal("Support Admin", detail.ActorDisplayName);
        Assert.Equal("AI organization", detail.OrganizationName);
        var changes = detail.Changes.ToDictionary(x => x.Field);
        Assert.Equal(("Open", "InProgress"), ((string?)changes["status"].Before, (string?)changes["status"].After));
        Assert.Equal(("Normal", "High"), ((string?)changes["priority"].Before, (string?)changes["priority"].After));
        Assert.Equal(adminId, changes["assignedTo"].After);
        Assert.Equal((1m, 2m), ((decimal?)changes["revision"].Before, (decimal?)changes["revision"].After));

        var feedbackAudit = (Guid)(await ScalarAsync("SELECT id FROM audit_logs WHERE target_entity='Feedback' AND new_values->>'operation'='FeedbackStatus'"))!;
        var feedbackDetail = (await audits.Get(adminId, af, feedbackAudit, default)).Value!;
        Assert.Equal("Duplicate of map bug", feedbackDetail.Reason);
        Assert.Contains(feedbackDetail.Changes, x => x.Field == "status" && (string?)x.Before == "Submitted" && (string?)x.After == "Reviewed");
        Assert.Contains(feedbackDetail.Changes, x => x.Field == "reviewedBy" && Equals(x.After, adminId));

        // Messages are audited without content; legacy or unmarked JSON never yields a reason or unknown fields.
        var messageAudit = (Guid)(await ScalarAsync("SELECT id FROM audit_logs WHERE new_values->>'operation'='Message'"))!;
        var messageDetail = (await audits.Get(adminId, af, messageAudit, default)).Value!;
        Assert.Null(messageDetail.Reason);
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(messageDetail));
        var legacy = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES('{legacy}','{tenant.User}','{tenant.Org}','System','Support','SupportTicket',gen_random_uuid(),gen_random_uuid(),'{{\"status\":\"Open\",\"reason\":\"token abc\",\"description\":\"iban 999\",\"supportAuditContract\":\"1\"}}',now())");
        var legacyDetail = (await audits.Get(adminId, af, legacy, default)).Value!;
        Assert.Null(legacyDetail.Reason);
        Assert.Equal("System", legacyDetail.ActorDisplayName);
        Assert.Equal(["status"], legacyDetail.Changes.Select(x => x.Field).ToArray());
        Assert.DoesNotContain("iban", System.Text.Json.JsonSerializer.Serialize(legacyDetail));
        var page = (await audits.List(adminId, af, new AuditFilters(OrganizationId: tenant.Org), default)).Value!;
        Assert.All(page.Items, x => Assert.Equal("AI organization", x.OrganizationName));
        Assert.Contains(page.Items, x => x.ActorDisplayName == "Support Admin");

        // Names are resolved at read time: a deleted organization becomes null without touching history.
        await ExecuteAsync($"UPDATE organizations SET deleted_at=now() WHERE id='{tenant.Org}'");
        Assert.Null((await audits.Get(adminId, af, ticketAudit, default)).Value!.OrganizationName);
        Assert.Equal(tenant.Org, (await audits.Get(adminId, af, ticketAudit, default)).Value!.OrganizationId);
    }
}
