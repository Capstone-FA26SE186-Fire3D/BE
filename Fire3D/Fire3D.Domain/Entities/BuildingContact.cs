using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class BuildingContact
{
    public Guid Id { get; set; }

    public Guid BuildingId { get; set; }

    public string ContactName { get; set; } = null!;

    public string? ContactRole { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Building Building { get; set; } = null!;
}
