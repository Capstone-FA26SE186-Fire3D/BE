namespace Fire3D.Domain.Entities;

public sealed class SupportTicketMessage
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid AuthorId { get; set; }
    public string Message { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
