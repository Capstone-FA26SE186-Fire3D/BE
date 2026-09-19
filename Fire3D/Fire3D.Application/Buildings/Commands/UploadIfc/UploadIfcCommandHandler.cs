using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UploadIfc;

internal sealed class UploadIfcCommandHandler(IBuildingStore store, TimeProvider clock)
    : IRequestHandler<UploadIfcCommand, AuthResult<RevisionResponse>>
{
    public async Task<AuthResult<RevisionResponse>> Handle(UploadIfcCommand command, CancellationToken ct)
    {
        var building = await store.FindBuildingAsync(command.BuildingId, command.OrganizationId, ct);
        if (building == null)
            return AuthResult<RevisionResponse>.Fail("NOT_FOUND", "Building not found.", 404);

        if (string.IsNullOrWhiteSpace(command.VersionLabel) || command.VersionLabel.Length > 100)
            return AuthResult<RevisionResponse>.Fail("VALIDATION_ERROR", "Version label is required (max 100 characters).", 400);

        var now = clock.GetUtcNow().UtcDateTime;
        
        var revisionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var revision = new Revision
        {
            Id = revisionId,
            BuildingId = command.BuildingId,
            OrganizationId = command.OrganizationId,
            UploadedBy = command.ActorId,
            VersionLabel = command.VersionLabel.Trim(),
            PrimaryType = FileType.IFC,
            Status = RevisionStatus.Processing, // Chuyển sang Processing để Worker xử lý
            CreatedAt = now,
            UpdatedAt = now
        };

        var document = new SourceDocument
        {
            Id = documentId,
            RevisionId = revisionId,
            UploadedBy = command.ActorId,
            OriginalFilename = command.OriginalFilename,
            FileSizeBytes = command.FileSizeBytes,
            StorageUrl = command.StorageUrl, // Đường dẫn lưu trữ (local filepath or blob url)
            MimeType = "application/x-step",
            Sha256Hash = command.Sha256Hash,
            FileType = FileType.IFC,
            QuarantineStatus = QuarantineStatus.Pending,
            CreatedAt = now
        };

        var job = new ProcessingJob
        {
            Id = jobId,
            RevisionId = revisionId,
            SourceDocumentId = documentId,
            Kind = "IFC_PROCESSING",
            JobKey = Guid.NewGuid(),
            Status = "Pending",
            AttemptNumber = 0,
            ToolchainVersion = "v1", // Có thể thiết lập từ cấu hình
            CreatedAt = now
        };

        await store.TryCreateRevisionAsync(revision, document, job, ct);
        await store.WriteAuditAsync(command.ActorId, command.OrganizationId, "revisions", revision.Id, "Upload", now, ct);

        var documentResponse = new SourceDocumentResponse(document.Id, document.OriginalFilename, document.FileSizeBytes, document.QuarantineStatus.ToString(), document.CreatedAt);
        var response = new RevisionResponse(revision.Id, revision.BuildingId, revision.VersionLabel, revision.Status.ToString(), revision.CreatedAt, documentResponse);

        return AuthResult<RevisionResponse>.Ok(response);
    }
}
