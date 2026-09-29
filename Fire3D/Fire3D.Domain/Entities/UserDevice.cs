using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class UserDevice
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceUuid { get; set; } = null!;

    public string? DeviceModel { get; set; }

    public string? OsVersion { get; set; }

    public string? AppVersion { get; set; }

    public string? FcmToken { get; set; }

    public Guid? InstallationId { get; set; }

    public bool NotificationsEnabled { get; set; }

    public int FcmTokenGeneration { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Session> Sessions { get; set; } = new List<Session>();

    public virtual User User { get; set; } = null!;

    public virtual DeviceInstallation? Installation { get; set; }
}
