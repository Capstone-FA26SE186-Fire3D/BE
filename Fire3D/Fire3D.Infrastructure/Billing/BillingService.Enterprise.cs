using Fire3D.Application.Authentication;
using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private static EnterpriseQuoteResponse EnterpriseView(EnterpriseQuoteRequest item)=>new(item.Id,item.OrganizationId,item.RequestedBuildingCount,
        item.RequestedDurationMonths,item.ContactName,item.ContactEmail,item.ContactPhone,item.Notes,item.Status,item.CreatedAt);
    public async Task<EnterpriseQuoteResponse> CreateEnterpriseRequest(Guid actor,EnterpriseQuoteRequestBody request,string? key,CancellationToken ct)
    {
        Field(request.RequestedBuildingCount>0,"requestedBuildingCount","Building count must be positive.");
        Field(request.RequestedDurationMonths is null or >0,"requestedDurationMonths","Duration must be positive when supplied.");
        Field(Text(request.ContactName,255),"contactName","Contact name is required, maximum 255 characters.");
        var email=PasswordResetValidation.NormalizeEmail(request.ContactEmail);Field(email is not null,"contactEmail","Supply a valid contact email.");
        Field(request.ContactPhone is null||Text(request.ContactPhone,50),"contactPhone","Phone must be non-empty, maximum 50 characters when supplied.");
        Field(request.Notes is null||Text(request.Notes,10000),"notes","Notes must be non-empty, maximum 10,000 characters when supplied.");
        request=request with{ContactName=request.ContactName.Trim(),ContactEmail=email!,ContactPhone=request.ContactPhone?.Trim(),Notes=request.Notes?.Trim()};
        var stableKey=Key(key);var hash=Hash(request);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await Authorize(actor,false,ct);
        if(identity.Role!=UserRole.OrganizationUser)throw new BillingException(403,"ORGANIZATION_USER_REQUIRED","An OrganizationUser creates their own enterprise quotation request.");
        var previous=await Receipt(identity,"CreateEnterpriseRequest",stableKey,hash,ct);
        if(previous is not null){var replay=Replay<EnterpriseQuoteResponse>(previous);await Scope(identity,replay.OrganizationId,ct);return replay;}
        var now=DateTime.UtcNow;var item=new EnterpriseQuoteRequest{Id=Guid.NewGuid(),OrganizationId=identity.OrganizationId!.Value,RequestedBy=actor,
            RequestedBuildingCount=request.RequestedBuildingCount,RequestedDurationMonths=request.RequestedDurationMonths,ContactName=request.ContactName,
            ContactEmail=request.ContactEmail,ContactPhone=request.ContactPhone,Notes=request.Notes,Status="New",CreatedAt=now,UpdatedAt=now,
            IdempotencyKey=$"enterprise:{actor}:{stableKey}"};
        db.Add(item);var response=EnterpriseView(item);SaveReceipt(identity,"CreateEnterpriseRequest",stableKey,hash,item.Id,response);
        Audit(identity,item.OrganizationId,"enterprise_quote_requests",item.Id,AuditAction.Create,null,new {item.Status,item.RequestedBuildingCount,item.RequestedDurationMonths});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<BillingPage<EnterpriseQuoteResponse>> ListEnterpriseRequests(Guid actor,int page,int pageSize,CancellationToken ct)
    {
        Pagination(page,pageSize);await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);var identity=await Authorize(actor,false,ct);
        var query=db.Set<EnterpriseQuoteRequest>().AsNoTracking().Where(x=>identity.Role==UserRole.PlatformAdmin||x.OrganizationId==identity.OrganizationId);
        var total=await query.CountAsync(ct);var items=await query.OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
        return new(items.Select(EnterpriseView).ToArray(),total,page,pageSize);
    }
}
