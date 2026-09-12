using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class BuildingLocation
{
    public Guid Id { get; set; }

    public Guid BuildingId { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? District { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public string? Geojson { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Building Building { get; set; } = null!;
}
