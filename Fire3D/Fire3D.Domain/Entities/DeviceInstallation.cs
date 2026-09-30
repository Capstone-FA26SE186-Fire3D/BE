namespace Fire3D.Domain.Entities;

/// <summary>Credential-bound installation identity. The client secret is stored only as a SHA-256 hash.</summary>
public sealed class DeviceInstallation
{
    public Guid Id { get; set; }
    public string DeviceUuid { get; set; } = null!;
    public string SecretHash { get; set; } = null!;
    public string? SecretHashScheme { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<UserDevice> UserDevices { get; set; } = new List<UserDevice>();
}
