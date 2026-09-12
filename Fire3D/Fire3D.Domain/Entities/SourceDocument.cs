using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// Phase 1: one accepted source IFC per revision; storage_url is a private stable object key, never a presigned URL.
/// </summary>
public partial class SourceDocument
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid UploadedBy { get; set; }

    public string OriginalFilename { get; set; } = null!;

    public long FileSizeBytes { get; set; }

    public string StorageUrl { get; set; } = null!;

    public string MimeType { get; set; } = null!;

    public string Sha256Hash { get; set; } = null!;

    public string? QuarantineNote { get; set; }

    public string UsageRights { get; set; } = null!;

    public string? SourceTool { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<ProcessingJob> ProcessingJobs { get; set; } = new List<ProcessingJob>();

    public virtual Revision Revision { get; set; } = null!;

    public virtual User UploadedByNavigation { get; set; } = null!;
}
