using Fire3D.Application.Scenarios;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Reporting;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Readiness_and_content_decisions_write_safe_atomic_audit_changes()
    {
        var f=await SeedReadyPackage();await using var db=BuildingContext(testConnection);var store=new ScenarioReadinessStore(db);
        var confirmed=await store.ExecuteAsync("Confirm",f.Owner,f.Owner,f.Version,f.Revision,new ConfirmTrainingRequest(f.Version,f.Run),null,default);
        Assert.True(confirmed.IsSuccess,confirmed.Error?.Code);
        var submitted=await store.ExecuteAsync("Submit",f.Owner,f.Owner,f.Version,null,new{},"audit-submit",default);
        Assert.True(submitted.IsSuccess,submitted.Error?.Code);
        var approved=await store.ExecuteAsync("Approve",adminId,adminId,f.Version,null,new {
            contentHash=submitted.Value.GetProperty("contentHash").GetString(),rubricHash=submitted.Value.GetProperty("rubricHash").GetString()},"audit-approve",default);
        Assert.True(approved.IsSuccess,approved.Error?.Code);
        var logs=await db.AuditLogs.AsNoTracking().Where(a=>a.TargetEntity=="ScenarioVersion" && a.TargetId==f.Version && a.Action!=AuditAction.Create).ToListAsync();
        Assert.Equal(3,logs.Count);
        var confirmation=Assert.Single(logs,a=>a.Action==AuditAction.ConfirmForTraining);
        var changes=SafeAuditChanges.Read(confirmation);
        Assert.Contains(changes,c=>c.Field=="candidateArtifactId" && Equals(c.After,f.Artifact));
        Assert.Contains(changes,c=>c.Field=="validationRunId" && Equals(c.After,f.Run));
        Assert.Contains(changes,c=>c.Field=="reviewedBy" && Equals(c.After,f.Owner));
        var decision=Assert.Single(logs,a=>a.UserId==adminId);
        Assert.Contains(SafeAuditChanges.Read(decision),c=>c.Field=="status" && Equals(c.Before,"Submitted") && Equals(c.After,"Approved"));
        Assert.Equal(f.Owner,confirmation.UserId);Assert.NotNull(confirmation.OrganizationId);
        Assert.All(logs,a=>{Assert.DoesNotContain("contentHash",a.NewValues!);Assert.DoesNotContain("reason",a.NewValues!);});
        var replay=await store.ExecuteAsync("Submit",f.Owner,f.Owner,f.Version,null,new{},"audit-submit",default);
        Assert.True(replay.IsSuccess);Assert.Equal(3,await db.AuditLogs.CountAsync(a=>a.TargetEntity=="ScenarioVersion" && a.TargetId==f.Version && a.Action!=AuditAction.Create));
    }
}

public sealed class ReadinessAuditProjectionTests
{
    [Fact]
    public void Legacy_and_sensitive_fields_do_not_become_changes()
    {
        var audit=new AuditLog { TargetEntity="ScenarioVersion",Action=AuditAction.Update,NewValues="{\"status\":\"Approved\",\"token\":\"secret\"}" };
        Assert.Empty(SafeAuditChanges.Read(audit));
        audit.NewValues="{\"auditContractVersion\":1,\"status\":\"Approved\",\"reason\":\"private message\",\"email\":\"private\",\"reviewedBy\":\"not-a-guid\"}";
        Assert.Equal("status",Assert.Single(SafeAuditChanges.Read(audit)).Field);
        audit.NewValues="{\"auditContractVersion\":\"secret\",\"status\":\"Approved\"}";
        Assert.Empty(SafeAuditChanges.Read(audit));
    }
}
