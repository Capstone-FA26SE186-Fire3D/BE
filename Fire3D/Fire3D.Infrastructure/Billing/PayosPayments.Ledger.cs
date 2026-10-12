using System.Globalization;
using System.Text.RegularExpressions;
using Fire3D.Application.Billing;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments
{
    private static readonly string[] CheckoutStatuses=["Creating","Ready","NeedsReconcile","Failed","Expired","Cancelled","Completed"];
    private static readonly string[] RequestStatuses=Enum.GetNames<PaymentRequestStatus>();

    // One row per checkout (or per payment request). Provisioning is aggregated in LATERAL subqueries so several provisioning
    // lines never multiply the payment row, and it understands Building, Upgrade and AI top-up lines. Paid alone is never
    // reported as provisioned. No QR, checkout URL, webhook payload or bank data is selected.
    private const string LedgerSelect="""
        SELECT op.id AS "CheckoutId",op.status AS "CheckoutStatus",op.last_error AS "CheckoutError",q.id AS "QuotationId",q.quotation_number AS "QuotationNumber",
         q.organization_id AS "OrganizationId",q.billing_purpose AS "Purpose",r.id AS "PaymentRequestId",r.status::text AS "PaymentStatus",t.id AS "TransactionId",
         t.status::text AS "TransactionStatus",COALESCE(r.expected_amount,q.total_amount) AS "Amount",COALESCE(r.expected_currency,q.currency) AS "Currency",
         CASE WHEN r.status IS DISTINCT FROM 'Paid' THEN 'NotStarted' WHEN pr.bad>0 THEN 'NeedsReconcile' WHEN pr.total=0 THEN 'Pending'
          WHEN pr.ok=pr.total AND pr.ok>=lines.expected THEN 'Succeeded' WHEN pr.ok>0 THEN 'Partial' ELSE 'Pending' END AS "ProvisioningStatus",
         lines.expected::int AS "ProvisioningLines",pr.ok::int AS "ProvisioningSucceeded",{0} AS "CreatedAt",r.paid_at AS "PaidAt"
        """;
    private const string LedgerJoins="""
         LEFT JOIN payment_transactions t ON t.id=r.paid_transaction_id
         CROSS JOIN LATERAL (SELECT count(p.id) total,count(p.id) FILTER(WHERE p.status='Succeeded') ok,count(p.id) FILTER(WHERE p.status='NeedsReconcile') bad
           FROM payment_provisioning_records p WHERE p.payment_transaction_id=t.id) pr
         CROSS JOIN LATERAL (SELECT CASE WHEN q.billing_purpose='AIQuotaTopUp' THEN (SELECT count(*) FROM quotation_topup_items i WHERE i.quotation_id=q.id)
           ELSE (SELECT count(*) FROM quotation_building_items i WHERE i.quotation_id=q.id) END expected) lines
        """;
    private const string CheckoutFrom=" FROM billing_checkout_operations op JOIN quotations q ON q.id=op.quotation_id LEFT JOIN payos_payment_requests r ON r.id=op.payment_request_id ";
    private const string CheckoutWhere=" WHERE (@org IS NULL OR q.organization_id=@org) AND (@quotation IS NULL OR q.id=@quotation) AND (@status IS NULL OR op.status=@status) AND (@from IS NULL OR op.created_at>=@from) AND (@to IS NULL OR op.created_at<@to) ";
    private const string RequestFrom=" FROM payos_payment_requests r JOIN quotations q ON q.id=r.quotation_id LEFT JOIN LATERAL (SELECT o.* FROM billing_checkout_operations o WHERE o.payment_request_id=r.id ORDER BY o.created_at,o.id LIMIT 1) op ON true ";
    private const string RequestWhere=" WHERE (@org IS NULL OR r.organization_id=@org) AND (@quotation IS NULL OR r.quotation_id=@quotation) AND (@status IS NULL OR r.status::text=@status) AND (@from IS NULL OR r.created_at>=@from) AND (@to IS NULL OR r.created_at<@to) ";

    private static DateTime? LedgerTime(string? value,string field)
    {
        if(value is null)return null;
        if(!Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant)||!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            throw new BillingException(400,"BILLING_VALIDATION_FAILED","Use a timezone-qualified timestamp.",new(){[field]=["Use a timezone-qualified timestamp, e.g. 2026-10-01T00:00:00Z."]});
        return date.UtcDateTime;
    }
    private static void LedgerPage(int page,int size){if(page<1||size is <1 or >100||page>1000000)throw new BillingException(400,"BILLING_PAGINATION_INVALID","page must be positive and pageSize must be 1–100.");}

    public async Task<BillingPage<PayosLedgerItem>> AdminCheckouts(Guid actorId,Guid family,PayosLedgerQuery query,CancellationToken ct)
    {
        LedgerPage(query.Page,query.PageSize);var from=LedgerTime(query.From,"from");var to=LedgerTime(query.To,"to");
        if(query.Status is not null&&!CheckoutStatuses.Contains(query.Status))throw new BillingException(400,"BILLING_VALIDATION_FAILED","Unknown checkout status.",new(){["status"]=[string.Join(", ",CheckoutStatuses)]});
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var actor=await ActorFor(actorId,false,ct);await Family(actorId,family,ct);if(actor.Role!=UserRole.PlatformAdmin)throw new BillingException(403,"BILLING_ROLE_FORBIDDEN","Only PlatformAdmin can list all checkouts.");
        return await Ledger(CheckoutFrom,CheckoutWhere,"op.created_at","op.created_at DESC,op.id",query.OrganizationId,query.QuotationId,query.Status,from,to,query.Page,query.PageSize,ct);
    }
    public async Task<BillingPage<PayosLedgerItem>> AdminRequests(Guid actorId,Guid family,PayosLedgerQuery query,CancellationToken ct)
    {
        LedgerPage(query.Page,query.PageSize);var from=LedgerTime(query.From,"from");var to=LedgerTime(query.To,"to");
        if(query.Status is not null&&!RequestStatuses.Contains(query.Status))throw new BillingException(400,"BILLING_VALIDATION_FAILED","Unknown payment request status.",new(){["status"]=[string.Join(", ",RequestStatuses)]});
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var actor=await ActorFor(actorId,false,ct);await Family(actorId,family,ct);if(actor.Role!=UserRole.PlatformAdmin)throw new BillingException(403,"BILLING_ROLE_FORBIDDEN","Only PlatformAdmin can list all payment requests.");
        return await Ledger(RequestFrom,RequestWhere,"r.created_at","r.created_at DESC,r.id",query.OrganizationId,query.QuotationId,query.Status,from,to,query.Page,query.PageSize,ct);
    }
    /// <summary>Checkout history of one quotation for the owning OrganizationUser or PlatformAdmin, so a reload finds its checkouts.</summary>
    public async Task<BillingPage<PayosLedgerItem>> QuotationCheckouts(Guid actorId,Guid family,Guid quotationId,int page,int pageSize,CancellationToken ct)
    {
        LedgerPage(page,pageSize);
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var actor=await ActorFor(actorId,false,ct);await Family(actorId,family,ct);
        var org=await db.Quotations.AsNoTracking().Where(x=>x.Id==quotationId).Select(x=>(Guid?)x.OrganizationId).SingleOrDefaultAsync(ct)??throw Missing();
        Scope(actor,org);
        return await Ledger(CheckoutFrom,CheckoutWhere,"op.created_at","op.created_at DESC,op.id",null,quotationId,null,null,null,page,pageSize,ct);
    }
    private async Task<BillingPage<PayosLedgerItem>> Ledger(string from,string where,string createdAt,string order,Guid? org,Guid? quotation,string? status,DateTime? start,DateTime? end,int page,int size,CancellationToken ct)
    {
        NpgsqlParameter[] Parameters()=>[
            new("org",NpgsqlDbType.Uuid){Value=(object?)org??DBNull.Value},new("quotation",NpgsqlDbType.Uuid){Value=(object?)quotation??DBNull.Value},
            new("status",NpgsqlDbType.Text){Value=(object?)status??DBNull.Value},new("from",NpgsqlDbType.TimestampTz){Value=(object?)start??DBNull.Value},
            new("to",NpgsqlDbType.TimestampTz){Value=(object?)end??DBNull.Value}];
        var total=await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\""+from+where,Parameters()).SingleAsync(ct);
        var items=await db.Database.SqlQueryRaw<PayosLedgerItem>(LedgerSelect.Replace("{0}",createdAt)+from+LedgerJoins+where+$" ORDER BY {order} OFFSET {(long)(page-1)*size} LIMIT {size}",Parameters()).ToListAsync(ct);
        return new(items,total,page,size);
    }
}
