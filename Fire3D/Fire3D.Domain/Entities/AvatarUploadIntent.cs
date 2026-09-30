namespace Fire3D.Domain.Entities;

public sealed class AvatarUploadIntent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string StagingObjectKey { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long ExpectedSizeBytes { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FinalObjectKey { get; set; }
    public long? ExpectedProfileRevision { get; set; }
    public Guid? CandidateAttemptId { get; set; }
    public string? CandidateObjectKey { get; set; }
    public string? CandidateSourceEtag { get; set; }
    public DateTime? CandidateLeaseUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}
