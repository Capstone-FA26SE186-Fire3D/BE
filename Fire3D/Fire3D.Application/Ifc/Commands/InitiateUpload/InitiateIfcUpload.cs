using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.InitiateUpload;

public sealed record InitiateIfcUploadRequest(long FileSizeBytes, string OriginalFilename, string VersionLabel);

public sealed record InitiateIfcUploadResponse(Guid RevisionId, string UploadUrl, string ObjectKey);

public sealed record InitiateIfcUploadCommand(Guid ActorId, Guid BuildingId, InitiateIfcUploadRequest Request) 
    : IRequest<AuthResult<InitiateIfcUploadResponse>>;

public sealed class InitiateIfcUploadHandler(IAuthStore accounts, IIfcWriteStore store, IStorageService storage)
    : IRequestHandler<InitiateIfcUploadCommand, AuthResult<InitiateIfcUploadResponse>>
{
    public async Task<AuthResult<InitiateIfcUploadResponse>> Handle(InitiateIfcUploadCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        var request = command.Request;
        if (request is null || request.FileSizeBytes <= 0 || string.IsNullOrWhiteSpace(request.OriginalFilename) || string.IsNullOrWhiteSpace(request.VersionLabel))
        {
            return AuthResult<InitiateIfcUploadResponse>.Fail("VALIDATION_ERROR", "Invalid upload parameters.", 400);
        }

        if (!request.OriginalFilename.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
        {
            return AuthResult<InitiateIfcUploadResponse>.Fail("VALIDATION_ERROR", "Only .ifc files are allowed.", 400);
        }

        // Generate Object Key
        var revisionId = Guid.NewGuid();
        var objectKey = $"buildings/{command.BuildingId:N}/revisions/{revisionId:N}/{Guid.NewGuid():N}.ifc";

        // Write to DB (Assuming store handles transaction, we just insert the revision)
        var result = await store.InitiateUploadAsync(command.ActorId, command.BuildingId, revisionId, request.VersionLabel, scope.Value.OrganizationId, ct);
        if (!result.IsSuccess) return new(default, result.Error);

        var url = await storage.GeneratePresignedUploadUrlAsync(objectKey, "application/octet-stream", TimeSpan.FromMinutes(60), ct);

        return AuthResult<InitiateIfcUploadResponse>.Ok(new(revisionId, url, objectKey));
    }
}
