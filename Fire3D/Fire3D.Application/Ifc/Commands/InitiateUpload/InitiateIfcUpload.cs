using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.InitiateUpload;

public sealed record InitiateIfcUploadRequest(long FileSizeBytes, string OriginalFilename, string VersionLabel, string? Sha256Hash = null);

public sealed record InitiateIfcUploadResponse(Guid RevisionId, string UploadUrl, string ObjectKey);

public sealed record InitiateIfcUploadCommand(Guid ActorId, Guid BuildingId, InitiateIfcUploadRequest Request, string? IdempotencyKey = null) 
    : IRequest<AuthResult<InitiateIfcUploadResponse>>;

public sealed class InitiateIfcUploadHandler(IAuthStore accounts, IIfcUploadService uploads)
    : IRequestHandler<InitiateIfcUploadCommand, AuthResult<InitiateIfcUploadResponse>>
{
    public async Task<AuthResult<InitiateIfcUploadResponse>> Handle(InitiateIfcUploadCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        return await uploads.InitiateAsync(command.ActorId, command.BuildingId, command.Request, command.IdempotencyKey, ct);
    }
}
