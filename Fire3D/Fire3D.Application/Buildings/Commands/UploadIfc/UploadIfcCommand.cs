using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UploadIfc;

public sealed record UploadIfcCommand(
    Guid ActorId, 
    Guid OrganizationId, 
    Guid BuildingId, 
    string VersionLabel, 
    string OriginalFilename, 
    long FileSizeBytes, 
    string StorageUrl, 
    string Sha256Hash) : IRequest<AuthResult<RevisionResponse>>;
