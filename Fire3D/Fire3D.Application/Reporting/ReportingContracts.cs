using Fire3D.Application.Authentication;
namespace Fire3D.Application.Reporting;
public sealed record AuditFilters(Guid? ActorId=null,Guid? OrganizationId=null,string? Action=null,string? TargetEntity=null,Guid? TargetId=null,Guid? CorrelationId=null,string? From=null,string? To=null,int Page=1,int PageSize=20);
/// <summary>ActorDisplayName/OrganizationName are current labels resolved at read time (null when the identity no longer exists); "System" for system actors.
/// ActorType classifies the actor (User or System) so a display name is never parsed to tell them apart.</summary>
public record AuditMetadata(Guid Id,Guid? UserId,Guid? OrganizationId,string Action,string TargetEntity,Guid? TargetId,Guid CorrelationId,DateTime CreatedAt,string? ActorDisplayName=null,string? OrganizationName=null,string? ActorType=null);
public sealed record AuditFieldChange(string Field,object? Before,object? After);
public sealed record AuditDetail : AuditMetadata
{
    public AuditDetail(AuditMetadata metadata,IReadOnlyList<AuditFieldChange> changes,string? reason=null)
        :base(metadata.Id,metadata.UserId,metadata.OrganizationId,metadata.Action,metadata.TargetEntity,metadata.TargetId,metadata.CorrelationId,metadata.CreatedAt,metadata.ActorDisplayName,metadata.OrganizationName,metadata.ActorType)
    {Changes=changes;Reason=reason;}
    public IReadOnlyList<AuditFieldChange> Changes { get; init; }
    /// <summary>Administrative reason recorded by a writer of a known contract version; null for legacy or unrecognised records.</summary>
    public string? Reason { get; init; }
}
public sealed record AuditPage(IReadOnlyList<AuditMetadata> Items,int Total,int Page,int PageSize,DateTime From,DateTime To);
public interface IAuditQueries
{
    Task<AuthResult<AuditPage>> List(Guid actor,Guid family,AuditFilters filters,CancellationToken ct);
    Task<AuthResult<AuditDetail>> Get(Guid actor,Guid family,Guid id,CancellationToken ct);
}
