namespace Fire3D.Domain.Entities;

public sealed class AvatarObjectCleanup
{
    public Guid Id { get; set; }
    public string ObjectKey { get; set; } = null!;
    public DateTime AvailableAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
}
