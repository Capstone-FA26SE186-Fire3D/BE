using System;
using System.Text.Json.Nodes;

namespace Fire3D.Domain.Entities;

public partial class RuntimeCompatibilityCatalog
{
    public Guid Id { get; set; }
    public string RuntimeVersion { get; set; } = null!;
    public string ProtocolVersion { get; set; } = null!;
    public string ManifestSchemaVersion { get; set; } = null!;
    public JsonNode Capabilities { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
