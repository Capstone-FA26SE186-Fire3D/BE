using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.FinalizeUpload;

public sealed record FinalizeIfcUploadRequest(string ObjectKey, long FileSizeBytes, string MimeType, string Sha256Hash, string OriginalFilename);

public sealed record FinalizeIfcUploadCommand(Guid ActorId, Guid RevisionId, FinalizeIfcUploadRequest Request) 
    : IRequest<AuthResult<bool>>;

public sealed class FinalizeIfcUploadHandler(IAuthStore accounts, IIfcUploadService uploads)
    : IRequestHandler<FinalizeIfcUploadCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(FinalizeIfcUploadCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        return await uploads.CompleteAsync(command.ActorId, command.RevisionId, command.Request, ct);
    }
}
