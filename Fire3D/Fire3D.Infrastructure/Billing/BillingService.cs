using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService(Fire3DDbContext db) : IBillingService
{
    private const decimal MaxMoney=999999999999m;
    private sealed record Actor(Guid Id,UserRole Role,Guid? OrganizationId);
    private static BillingException Missing()=>new(404,"BILLING_RESOURCE_NOT_FOUND","The billing resource was not found in your scope.");
    private static BillingException Conflict(string message)=>new(409,"BILLING_STATE_CONFLICT",message);
    private static void Field(bool valid,string field,string message)
    {
        if(!valid) throw new BillingException(400,"BILLING_VALIDATION_FAILED",message,new() {[field]=[message]});
    }
    private static bool Money(decimal value)=>value>=0 && value<=MaxMoney && value==decimal.Truncate(value);
    private static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value) && value.Trim().Length<=max && !value.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t');
    private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private async Task IdentityLock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0))",ct);
    private async Task Lock(string key,bool shared,CancellationToken ct)
    {
        if(shared) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({key},0))",ct);
        else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))",ct);
    }
    private async Task<Actor> Authorize(Guid id,bool admin,CancellationToken ct)
    {
        var actor=await db.Users.AsNoTracking().Where(x=>x.Id==id&&x.IsActive&&x.DeletedAt==null)
            .Select(x=>new Actor(x.Id,x.Role,x.OrganizationId)).SingleOrDefaultAsync(ct)
            ?? throw new BillingException(401,"ACCOUNT_UNAVAILABLE","The account is no longer active.");
        if(admin && actor.Role!=UserRole.PlatformAdmin || actor.Role is not (UserRole.PlatformAdmin or UserRole.OrganizationUser))
            throw new BillingException(403,"BILLING_ROLE_FORBIDDEN","This operation is not allowed for your account role.");
        if(actor.Role==UserRole.OrganizationUser && !await db.Organizations.AnyAsync(x=>x.Id==actor.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct))
            throw new BillingException(403,"ORGANIZATION_UNAVAILABLE","The organization is no longer active.");
        return actor;
    }
    private async Task Scope(Actor actor,Guid org,CancellationToken ct)
    {
        if(actor.Role!=UserRole.PlatformAdmin&&actor.OrganizationId!=org) throw Missing();
        if(!await db.Organizations.AnyAsync(x=>x.Id==org&&x.IsActive&&x.DeletedAt==null,ct)) throw Missing();
    }
    private void Audit(Actor actor,Guid? org,string entity,Guid id,AuditAction action,object? old,object? current)
        =>db.AuditLogs.Add(new AuditLog {Id=Guid.NewGuid(),UserId=actor.Id,OrganizationId=org,ActorType="User",TargetEntity=entity,
            TargetId=id,Action=action,CorrelationId=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,
            OldValues=old is null ? null:JsonSerializer.Serialize(old),NewValues=current is null ? null:JsonSerializer.Serialize(current)});
    private static string Key(string? value)
    {
        Field(value is not null&&value.Length is >=1 and <=128&&value.All(c=>c is >= '!' and <= '~'),"idempotencyKey","Supply an Idempotency-Key of 1–128 printable ASCII characters, without spaces.");
        return value!;
    }
    private async Task<BillingCommandReceipt?> Receipt(Actor actor,string operation,string key,string hash,CancellationToken ct)
    {
        await Lock($"fet3d:billing:receipt:{actor.Id}:{operation}:{key}",false,ct);
        var receipt=await db.Set<BillingCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x=>x.ActorId==actor.Id&&x.Operation==operation&&x.IdempotencyKey==key,ct);
        if(receipt is not null&&receipt.InputHash!=hash) throw new BillingException(409,"IDEMPOTENCY_KEY_CONFLICT","The idempotency key was already used for different input.");
        return receipt;
    }
    private void SaveReceipt<T>(Actor actor,string operation,string key,string hash,Guid resource,T response)
        =>db.Add(new BillingCommandReceipt{Id=Guid.NewGuid(),ActorId=actor.Id,Operation=operation,IdempotencyKey=key,InputHash=hash,ResourceId=resource,Response=JsonSerializer.Serialize(response),CreatedAt=DateTime.UtcNow});
    private static T Replay<T>(BillingCommandReceipt receipt)=>receipt.Response is null ? throw Conflict("The legacy receipt has no replay response.") : JsonSerializer.Deserialize<T>(receipt.Response)!;
    private static void Pagination(int page,int size) {Field(page>=1,"page","Page must be positive.");Field(size is >=1 and <=100,"pageSize","Page size must be between 1 and 100.");Field((long)(page-1)*size<=int.MaxValue,"page","Page is too large.");}

    public async Task<IReadOnlyList<PackageResponse>> ListPackages(Guid actor,CancellationToken ct)
    {
        var identity=await Authorize(actor,false,ct);
        var packages=await db.ServicePackages.AsNoTracking().Where(x=>identity.Role==UserRole.PlatformAdmin||x.IsActive).OrderBy(x=>x.Code).ToListAsync(ct);
        var now=DateTime.UtcNow;
        var policies=await db.BillingQuotaPolicies.AsNoTracking().Where(x=>x.EffectiveFrom<=now&&(x.EffectiveUntil==null||x.EffectiveUntil>now)).Select(x=>x.Id).ToListAsync(ct);
        return packages.Select(x=>{
            var value=PackageView(x);
            return value with {IsPurchasable=value.IsPurchasable && (x.AiPolicyVersionId is null || policies.Contains(x.AiPolicyVersionId.Value))};
        }).ToArray();
    }
    private static PackageResponse PackageView(ServicePackage x)=>new(x.Id,x.Code,x.Name,x.UnitPrice,x.Currency,x.DurationMonths??0,x.IsActive,x.Description,x.Revision,
        x.CommercialVersion,x.LearnerLimit,x.AiQuotaUnits,x.AiPolicyVersionId,"Monthly",x.IsActive && x.CommercialVersion==7 && x.DurationMonths is 6 or 12 && x.LearnerLimit>0 && x.AiQuotaUnits>=0 && (x.AiQuotaUnits==0 || x.AiPolicyVersionId.HasValue));
    private static DiscountResponse DiscountView(ServicePackageDiscountRule x)=>new(x.Id,x.Code,x.DiscountKind,x.DiscountValue,x.MinimumBuildings,x.ValidFrom,x.ValidUntil,x.ServicePackageId,x.MinimumDurationMonths,x.IsActive,x.Revision);
    private static PackageWriteRequest ValidatePackage(PackageWriteRequest request)
    {
        Field(Text(request.Code,50)&&System.Text.RegularExpressions.Regex.IsMatch(request.Code.Trim(),"^[A-Za-z0-9][A-Za-z0-9_-]*$"),"code","Use a package code of 1–50 letters, numbers, underscores or hyphens.");
        Field(Text(request.Name,255),"name","Name is required and must be at most 255 characters.");
        Field(Money(request.UnitPrice),"unitPrice","Monthly unit price must be a non-negative whole VND amount within the supported range.");
        Field(request.DurationMonths is 6 or 12,"durationMonths","V7 Building packages require 6 or 12 months.");
        Field(request.LearnerLimit>0,"learnerLimit","Learner limit must be positive.");
        Field(request.AiQuotaUnits>=0,"aiQuotaUnits","AI quota units must be supplied and non-negative.");
        Field(request.AiQuotaUnits==0 || request.AiPolicyVersionId.HasValue,"aiPolicyVersionId","Quota-bearing packages require a policy version.");
        Field(request.Description is null||Text(request.Description,10000),"description","Description must be non-empty and at most 10,000 characters when supplied.");
        return request with {Code=request.Code.Trim().ToUpperInvariant(),Name=request.Name.Trim(),Description=request.Description?.Trim()};
    }
    public async Task<PackageResponse> SavePackage(Guid actor,Guid family,Guid? id,PackageWriteRequest request,string? ifMatch,CancellationToken ct)
    {
        request=ValidatePackage(request);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        await Lock("fet3d:billing:catalog",false,ct);
        var item=id.HasValue ? await db.ServicePackages.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing() : new ServicePackage {Id=Guid.NewGuid(),CreatedBy=actor,CreatedAt=DateTime.UtcNow,Features="{}",Currency="VND"};
        var old=id.HasValue ? PackageView(item):null;
        if(id.HasValue) BillingETag.Require(ifMatch,item.Id,item.Revision);
        if(await db.ServicePackages.AnyAsync(x=>x.Code==request.Code&&x.Id!=item.Id,ct)) throw new BillingException(409,"PACKAGE_CODE_EXISTS","The package code already exists.");
        if(request.AiPolicyVersionId.HasValue && !await db.BillingQuotaPolicies.AnyAsync(x=>x.Id==request.AiPolicyVersionId && x.EffectiveFrom<=DateTime.UtcNow && (x.EffectiveUntil==null || x.EffectiveUntil>DateTime.UtcNow),ct))
            throw new BillingException(400,"BILLING_POLICY_INVALID","Choose an effective quota policy.",new(){["aiPolicyVersionId"]=["Policy is absent or outside its effective interval."]});
        item.CommercialVersion=7;item.LearnerLimit=request.LearnerLimit;item.AiQuotaUnits=request.AiQuotaUnits;item.AiPolicyVersionId=request.AiPolicyVersionId;
        item.Code=request.Code;item.Name=request.Name;item.UnitPrice=request.UnitPrice;item.DurationMonths=request.DurationMonths;item.IsActive=request.IsActive;item.Description=request.Description;item.UpdatedAt=DateTime.UtcNow;
        if(id.HasValue)item.Revision++;else db.ServicePackages.Add(item);
        var response=PackageView(item);Audit(identity,null,"service_packages",item.Id,id.HasValue?AuditAction.Update:AuditAction.Create,old,response);
        await RequireLiveFamily(actor,family,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<IReadOnlyList<DiscountResponse>> ListDiscounts(Guid actor,CancellationToken ct)
    {
        await Authorize(actor,true,ct);
        var items=await db.Set<ServicePackageDiscountRule>().AsNoTracking().OrderBy(x=>x.Code).ToListAsync(ct);return items.Select(DiscountView).ToArray();
    }
    public async Task<DiscountResponse> SaveDiscount(Guid actor,Guid family,Guid? id,DiscountWriteRequest request,string? ifMatch,CancellationToken ct)
    {
        Field(Text(request.Code,80),"code","Discount code is required, maximum 80 characters.");
        Field(request.DiscountKind is "Percent" or "Fixed","discountKind","Choose Percent or Fixed.");
        Field(request.DiscountValue>=0&&(request.DiscountKind=="Percent" ? request.DiscountValue<=100&&decimal.Round(request.DiscountValue,2)==request.DiscountValue : Money(request.DiscountValue)),"discountValue","Use a percent from 0–100 with at most two decimals or a non-negative whole VND amount.");
        Field(request.MinimumBuildings>0,"minimumBuildings","Minimum Building count must be positive.");
        Field(request.MinimumDurationMonths is null or >0,"minimumDurationMonths","Minimum duration must be positive when supplied.");
        Field(request.ValidFrom!=default,"validFrom","Supply validFrom as an ISO 8601 timestamp with timezone.");
        Field(request.ValidUntil is null||request.ValidUntil>request.ValidFrom,"validUntil","Expiry must be after validFrom.");
        request=request with {Code=request.Code.Trim().ToUpperInvariant()};
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);await Lock("fet3d:billing:catalog",false,ct);
        if(request.ServicePackageId.HasValue&&!await db.ServicePackages.AnyAsync(x=>x.Id==request.ServicePackageId,ct))throw Missing();
        var item=id.HasValue ? await db.Set<ServicePackageDiscountRule>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing() : new ServicePackageDiscountRule {Id=Guid.NewGuid(),CreatedBy=actor,CreatedAt=DateTime.UtcNow};
        var old=id.HasValue?DiscountView(item):null;if(id.HasValue)BillingETag.Require(ifMatch,item.Id,item.Revision);
        if(await db.Set<ServicePackageDiscountRule>().AnyAsync(x=>x.Code==request.Code&&x.Id!=item.Id,ct))throw new BillingException(409,"DISCOUNT_CODE_EXISTS","The discount code already exists.");
        item.Code=request.Code;item.DiscountKind=request.DiscountKind;item.DiscountValue=request.DiscountValue;item.DiscountCurrency=request.DiscountKind=="Fixed"?"VND":null;
        item.MinimumBuildings=request.MinimumBuildings;item.MinimumDurationMonths=request.MinimumDurationMonths;item.ServicePackageId=request.ServicePackageId;
        item.ValidFrom=request.ValidFrom.UtcDateTime;item.ValidUntil=request.ValidUntil?.UtcDateTime;item.IsActive=request.IsActive;item.UpdatedAt=DateTime.UtcNow;
        if(id.HasValue)item.Revision++;else db.Add(item);
        var response=DiscountView(item);Audit(identity,null,"service_package_discount_rules",item.Id,id.HasValue?AuditAction.Update:AuditAction.Create,old,response);
        await RequireLiveFamily(actor,family,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
}
