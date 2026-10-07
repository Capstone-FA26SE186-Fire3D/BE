using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;
using Microsoft.Extensions.Options;

namespace Fire3D.AuthTests;

public sealed class IfcUploadBindingTests
{
    private static readonly User Actor = new() { Id = Guid.NewGuid(), Role = UserRole.OrganizationUser, OrganizationId = Guid.NewGuid(), IsActive = true };
    private static IAuthStore Accounts() => ResetProxy.For<IAuthStore>((method, _) => method switch
    {
        "FindUserAsync" => Task.FromResult<User?>(Actor), "OrganizationIsActiveAsync" => Task.FromResult(true),
        _ => throw new InvalidOperationException(method)
    });
    [Fact]
    public async Task Complete_checks_persisted_intent_before_any_storage_access()
    {
        var storage = ResetProxy.For<IStorageService>((_, _) => throw new Exception("Must not read arbitrary object before intent authorization"));
        var store = ResetProxy.For<IIfcUploadStore>((method, _) => method == "ReadAsync"
            ? Task.FromResult(AuthResult<IfcUploadIntent>.Fail("IFC_UPLOAD_INTENT_NOT_FOUND", "Upload intent was not found.", 404))
            : throw new InvalidOperationException(method));
        var uploads = Flow(store, storage);
        var result = await new FinalizeIfcUploadHandler(Accounts(), uploads).Handle(new(Actor.Id, Guid.NewGuid(),
            new("another-user/source.ifc", 10, "application/octet-stream", new string('a',64), "model.ifc")), default);
        Assert.Equal(404, result.Error?.Status);
    }
    [Fact]
    public async Task Initiate_rejects_missing_expected_sha_before_creating_revision()
    {
        var storage = ResetProxy.For<IStorageService>((_, _) => throw new Exception("Must not issue URL for unbound source"));
        var store = ResetProxy.For<IIfcUploadStore>((_, _) => throw new Exception("Expected SHA-256 is required before persisting intent"));
        var result = await new InitiateIfcUploadHandler(Accounts(), Flow(store, storage)).Handle(new(Actor.Id, Guid.NewGuid(), new(10,"model.ifc","v1")), default);
        Assert.Equal(400, result.Error?.Status);
    }
    private static IfcUploadService Flow(IIfcUploadStore store, IStorageService storage) => new(store, storage,
        ResetProxy.For<IIfcSourceInspector>((_, _) => throw new Exception("Must not inspect unauthorized source")),
        Options.Create(new IfcUploadOptions { Enabled = true, MaxBytes = 1024 }), TimeProvider.System);

    [Fact]
    public async Task Complete_validation_reports_fields_and_never_calls_store_or_storage()
    {
        var result = await Flow(ResetProxy.For<IIfcUploadStore>((_, _) => throw new Exception("Invalid request")),
            ResetProxy.For<IStorageService>((_, _) => throw new Exception("Invalid request")))
            .CompleteAsync(Actor.Id, Guid.NewGuid(), new("", 0, "text/plain", "hash", ""), default);
        Assert.Equal(400, result.Error?.Status);
        Assert.Equal(new[] { "fileSizeBytes", "mimeType", "objectKey", "originalFilename", "sha256Hash" }, result.Error!.Errors!.Keys.Order());
    }
}
