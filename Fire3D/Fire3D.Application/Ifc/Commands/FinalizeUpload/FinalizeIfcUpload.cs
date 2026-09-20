using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.FinalizeUpload;

public sealed record FinalizeIfcUploadRequest(string ObjectKey, long FileSizeBytes, string MimeType, string Sha256Hash, string OriginalFilename);

public sealed record FinalizeIfcUploadCommand(Guid ActorId, Guid RevisionId, FinalizeIfcUploadRequest Request) 
    : IRequest<AuthResult<bool>>;

public sealed class FinalizeIfcUploadHandler(IAuthStore accounts, IIfcWriteStore store, IStorageService storage)
    : IRequestHandler<FinalizeIfcUploadCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(FinalizeIfcUploadCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        var request = command.Request;
        if (request is null || string.IsNullOrWhiteSpace(request.ObjectKey) || request.FileSizeBytes <= 0 || string.IsNullOrWhiteSpace(request.Sha256Hash))
        {
            return AuthResult<bool>.Fail("VALIDATION_ERROR", "Invalid finalize parameters.", 400);
        }

        // Verify file on S3
        var exists = await storage.VerifyObjectExistsAsync(request.ObjectKey, request.FileSizeBytes, ct);
        if (!exists)
        {
            return AuthResult<bool>.Fail("FILE_NOT_FOUND", "The uploaded file could not be verified on the storage server.", 422);
        }

        // Save SourceDocument
        return await store.FinalizeUploadAsync(command.ActorId, command.RevisionId, request, scope.Value.OrganizationId, ct);
    }
}
