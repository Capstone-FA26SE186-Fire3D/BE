using System.Security.Cryptography;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class IfcUploadServiceBranchTests
{
    private static readonly byte[] Bytes = "ISO-10303-21;HEADER;ENDSEC;"u8.ToArray();
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));
    private static T Unexpected<T>() where T : class => StubProxy.For<T>((method, _) => throw new Exception("Unexpected: " + method));
    private static IfcUploadService Service(IIfcUploadStore store, IIfcSourceInspector inspector, IfcUploadOptions? options = null) =>
        new(store, Unexpected<IStorageService>(), inspector, Options.Create(options ?? new IfcUploadOptions { Enabled = true, MaxBytes = 1024 }), TimeProvider.System);
    private static FinalizeIfcUploadRequest Complete() => new("ifc/staging/model.ifc", Bytes.Length, "application/octet-stream", Hash, "model.ifc");
    private static IfcUploadIntent Intent(long size) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ifc/staging/model.ifc", size, Hash, "model.ifc", DateTime.UtcNow.AddMinutes(10), false);

    // Persisted intent, then a claim; Adopt must never run when the copy cannot be verified.
    private static IIfcUploadStore Store(IfcUploadIntent intent) => StubProxy.For<IIfcUploadStore>((method, _) => method switch
    {
        nameof(IIfcUploadStore.ReadAsync) => Task.FromResult(AuthResult<IfcUploadIntent>.Ok(intent)),
        nameof(IIfcUploadStore.ClaimAsync) => Task.FromResult(AuthResult<IfcUploadAttempt>.Ok(new(Guid.NewGuid(), "ifc/final/model.ifc"))),
        _ => throw new Exception("Unexpected: " + method)
    });

    private static IIfcSourceInspector Inspector(Func<string, object?[], object?> final) => StubProxy.For<IIfcSourceInspector>((method, args) =>
        (string)args[0]! == "ifc/final/model.ifc" ? final(method, args) : method switch
        {
            nameof(IIfcSourceInspector.MetadataAsync) => Task.FromResult<StorageObjectMetadata?>(new(Bytes.Length, "application/octet-stream", "staging-etag")),
            nameof(IIfcSourceInspector.InspectAsync) => Task.FromResult<IfcObjectDigest?>(new(Bytes.Length, Hash, "staging-etag")),
            nameof(IIfcSourceInspector.CopyAsync) => Task.FromResult(true),
            _ => throw new Exception("Unexpected: " + method)
        });

    [Fact]
    public async Task Initiate_rejects_oversize_file_and_blank_version_label_before_the_store()
    {
        var service = Service(Unexpected<IIfcUploadStore>(), Unexpected<IIfcSourceInspector>(), new IfcUploadOptions { Enabled = true, MaxBytes = 10 });
        var result = await service.InitiateAsync(Guid.NewGuid(), Guid.NewGuid(), new(11, "model.ifc", "  ", Hash), "key", default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Equal(400, result.Error?.Status);
        Assert.Equal(["fileSizeBytes", "versionLabel"], result.Error!.Errors!.Keys.Order());
    }

    [Fact]
    public async Task Initiate_is_unavailable_while_IFC_upload_is_disabled()
    {
        var service = Service(Unexpected<IIfcUploadStore>(), Unexpected<IIfcSourceInspector>(), new IfcUploadOptions { Enabled = false, MaxBytes = 1024 });
        var result = await service.InitiateAsync(Guid.NewGuid(), Guid.NewGuid(), new(Bytes.Length, "model.ifc", "v1", Hash), "key", default);
        Assert.Equal("IFC_UPLOAD_DISABLED", result.Error?.Code);
        Assert.Equal(503, result.Error?.Status);
    }

    [Fact]
    public async Task Complete_refuses_an_intent_larger_than_the_configured_limit_without_reading_storage()
    {
        var result = await Service(Store(Intent(2048)), Unexpected<IIfcSourceInspector>()).CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), Complete(), default);
        Assert.Equal("IFC_UPLOAD_DISABLED", result.Error?.Code);
        Assert.Equal(503, result.Error?.Status);
    }

    [Fact]
    public async Task Complete_rejects_a_copy_that_is_missing_or_hashes_differently_and_never_adopts_it()
    {
        var missing = Inspector((method, _) => method == nameof(IIfcSourceInspector.MetadataAsync) ? Task.FromResult<StorageObjectMetadata?>(null) : throw new Exception("Unexpected: " + method));
        var result = await Service(Store(Intent(Bytes.Length)), missing).CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), Complete(), default);
        Assert.Equal(("IFC_SOURCE_MISMATCH", 422, "Copied source could not be verified."), (result.Error?.Code, result.Error?.Status, result.Error?.Message));

        var changed = Inspector((method, _) => method switch
        {
            nameof(IIfcSourceInspector.MetadataAsync) => Task.FromResult<StorageObjectMetadata?>(new(Bytes.Length, "application/octet-stream", "final-etag")),
            nameof(IIfcSourceInspector.InspectAsync) => Task.FromResult<IfcObjectDigest?>(new(Bytes.Length, new string('b', 64), "final-etag")),
            _ => throw new Exception("Unexpected: " + method)
        });
        result = await Service(Store(Intent(Bytes.Length)), changed).CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), Complete(), default);
        Assert.Equal(("IFC_SOURCE_MISMATCH", 422, "Copied source hash does not match the intent."), (result.Error?.Code, result.Error?.Status, result.Error?.Message));
    }

    [Fact]
    public async Task Complete_reports_storage_timeout_and_provider_failure_as_retryable_but_propagates_caller_cancellation()
    {
        foreach (var failure in new Exception[] { new OperationCanceledException("storage deadline"), new IfcStorageFailure(new IOException("provider down")) })
        {
            var inspector = StubProxy.For<IIfcSourceInspector>((_, _) => Task.FromException<StorageObjectMetadata?>(failure));
            var result = await Service(Store(Intent(Bytes.Length)), inspector).CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), Complete(), default);
            Assert.Equal("IFC_STORAGE_UNAVAILABLE", result.Error?.Code);
            Assert.Equal(503, result.Error?.Status);
        }
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var aborted = StubProxy.For<IIfcSourceInspector>((_, args) => Task.FromCanceled<StorageObjectMetadata?>((CancellationToken)args[1]!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(Store(Intent(Bytes.Length)), aborted).CompleteAsync(Guid.NewGuid(), Guid.NewGuid(), Complete(), cancelled.Token));
    }
}
