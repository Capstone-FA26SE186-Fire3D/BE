using Fire3D.Application.Billing;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

/// <summary>Reminders addressed to the calling OrganizationUser in their current organization; nothing else is readable.</summary>
public sealed class OrganizationNotifications(Fire3DDbContext db) : IOrganizationNotifications
{
    private static readonly string[] Types=["BuildingServiceExpiring"];
    private const string Projection="""
        SELECT n.id AS "Id",n.notification_type AS "Type",n.title AS "Title",n.body AS "Body",n.building_id AS "BuildingId",b.name AS "BuildingName",
         n.entitlement_id AS "EntitlementId",n.reference_ends_at AS "ReferenceEndsAt",n.created_at AS "CreatedAt",n.read_at AS "ReadAt",
         (e.status='Active' AND e.ends_at>clock_timestamp() AND NOT EXISTS(SELECT 1 FROM service_entitlements r WHERE r.building_id=n.building_id AND r.id<>n.entitlement_id
           AND r.payment_transaction_id IS NOT NULL AND r.status IN('Active','Suspended') AND r.starts_at>=n.reference_ends_at)) AS "StillRelevant"
        FROM organization_notifications n JOIN service_entitlements e ON e.id=n.entitlement_id LEFT JOIN buildings b ON b.id=n.building_id AND b.deleted_at IS NULL
        """;

    private async Task<Guid> Recipient(Guid actor,Guid family,CancellationToken ct)
    {
        var user=await db.Users.AsNoTracking().Where(x=>x.Id==actor&&x.IsActive&&x.DeletedAt==null).Select(x=>new {x.Role,x.OrganizationId}).SingleOrDefaultAsync(ct)
            ??throw new BillingException(401,"ACCOUNT_UNAVAILABLE","The account is no longer active.");
        if(user.Role!=UserRole.OrganizationUser||user.OrganizationId is null)throw new BillingException(403,"ORGANIZATION_USER_REQUIRED","Only OrganizationUser accounts receive organization notifications.");
        if(!await db.Organizations.AnyAsync(x=>x.Id==user.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct))throw new BillingException(403,"ORGANIZATION_UNAVAILABLE","The organization is no longer active.");
        if(family==Guid.Empty||!await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id={actor} AND family_id={family} AND revoked_at IS NULL AND consumed_at IS NULL AND expires_at>clock_timestamp()) AS "Value" """).SingleAsync(ct))
            throw new BillingException(401,"SESSION_REVOKED","The session is no longer active.");
        return user.OrganizationId.Value;
    }

    public async Task<BillingPage<OrganizationNotificationResponse>> List(Guid actor,Guid family,NotificationQuery query,CancellationToken ct)
    {
        if(query.Page<1||query.PageSize is <1 or >100||query.Page>1000000)throw new BillingException(400,"BILLING_PAGINATION_INVALID","page must be positive and pageSize must be 1–100.");
        if(query.Type is not null&&!Types.Contains(query.Type))throw new BillingException(400,"BILLING_VALIDATION_FAILED","Unknown notification type.",new(){["type"]=Types});
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var org=await Recipient(actor,family,ct);
        var unread=query.Unread;var type=query.Type;
        var total=await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM organization_notifications n WHERE n.recipient_user_id={actor} AND n.organization_id={org}
             AND ({unread}::boolean IS NULL OR (n.read_at IS NULL)={unread}::boolean) AND ({type}::text IS NULL OR n.notification_type={type}::text)
            """).SingleAsync(ct);
        var skip=(long)(query.Page-1)*query.PageSize;
        var items=await db.Database.SqlQueryRaw<OrganizationNotificationResponse>(Projection+"""
             WHERE n.recipient_user_id={0} AND n.organization_id={1} AND ({2}::boolean IS NULL OR (n.read_at IS NULL)={2}::boolean) AND ({3}::text IS NULL OR n.notification_type={3}::text)
             ORDER BY n.created_at DESC,n.id OFFSET {4} LIMIT {5}
            """,actor,org,(object?)unread??DBNull.Value,(object?)type??DBNull.Value,skip,query.PageSize).ToListAsync(ct);
        return new(items,total,query.Page,query.PageSize);
    }

    /// <summary>Idempotent: the first read time is kept; another recipient's or tenant's notification is 404.</summary>
    public async Task<OrganizationNotificationResponse> MarkRead(Guid actor,Guid family,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var org=await Recipient(actor,family,ct);
        var updated=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organization_notifications SET read_at=COALESCE(read_at,clock_timestamp()) WHERE id={id} AND recipient_user_id={actor} AND organization_id={org}",ct);
        if(updated==0)throw new BillingException(404,"NOTIFICATION_NOT_FOUND","The notification was not found.");
        var item=await db.Database.SqlQueryRaw<OrganizationNotificationResponse>(Projection+" WHERE n.id={0}",id).SingleAsync(ct);
        await tx.CommitAsync(ct);return item;
    }
}
