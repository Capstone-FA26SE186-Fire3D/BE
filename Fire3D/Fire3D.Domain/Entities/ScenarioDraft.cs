using System;
using System.Text.Json.Nodes;

namespace Fire3D.Domain.Entities;

public partial class ScenarioDraft
{
    public Guid Id { get; set; }
    public Guid ScenarioId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BuildingId { get; set; }
    public int DraftNumber { get; set; }
    public JsonNode State { get; set; } = null!;
    public string Source { get; set; } = null!;
    public Guid? LastAiRequestId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    // xmin equivalent for EF Core concurrency token (uint in Postgres)
    public uint Version { get; set; }

    public virtual Scenario Scenario { get; set; } = null!;
    public virtual Revision Revision { get; set; } = null!;
    public virtual Organization Organization { get; set; } = null!;
    public virtual Building Building { get; set; } = null!;
    public virtual User CreatedByNavigation { get; set; } = null!;
}
