using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.IfcTests;

// #214: upload-complete reads FILE_SCHEMA from the STEP header while it hashes the file.
public sealed class IfcStepScannerTests
{
    private static string Model(string schemaClause) =>
        $"ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\r\nFILE_NAME('a.ifc','2026-11-23',(''),(''),'','','');\r\n{schemaClause}\r\nENDSEC;\r\nDATA;\r\n#1=IFCPROJECT('x',$,'p',$,$,$,$,$,$);\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n";

    [Theory]
    [InlineData("FILE_SCHEMA(('IFC2X3'));", "IFC2X3")]
    [InlineData("FILE_SCHEMA(('IFC2X3_TC1'));", "IFC2X3_TC1")]
    [InlineData("FILE_SCHEMA(('IFC4'));", "IFC4")]
    [InlineData("file_schema ( ( 'ifc4' ) ) ;", "IFC4")]
    [InlineData("FILE_SCHEMA(('IFC4_ADD2_TC1'));", "IFC4_ADD2_TC1")]
    [InlineData("FILE_SCHEMA(('IFC4X3'));", "IFC4X3")]
    [InlineData("FILE_SCHEMA(('IFC4X3_ADD2'));", "IFC4X3_ADD2")]
    public void Supported_schemas_and_their_variants_pass(string clause, string schema)
    {
        var summary = IfcStepScanner.Scan(Encoding.ASCII.GetBytes(Model(clause)));
        Assert.True(summary.StepHeader);
        Assert.Equal(schema, summary.Schema);
        Assert.True(summary.SchemaSupported);
        Assert.Null(IfcUploadService.ContentRejection(summary));
    }

    [Theory]
    [InlineData("FILE_SCHEMA(('AP214'));", "AP214")]
    [InlineData("FILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));", "CONFIG_CONTROL_DESIGN")]
    [InlineData("FILE_SCHEMA(('IFC4X1'));", "IFC4X1")]
    [InlineData("FILE_SCHEMA(('IFC2X2_FINAL'));", "IFC2X2_FINAL")]
    [InlineData("FILE_SCHEMA(('IFC5'));", "IFC5")]
    [InlineData("FILE_SCHEMA(('IFC4X3 ADD2'));", "IFC4X3 ADD2")]
    [InlineData("", null)]
    public void Other_or_missing_schemas_are_rejected_as_unsupported(string clause, string? schema)
    {
        var summary = IfcStepScanner.Scan(Encoding.ASCII.GetBytes(Model(clause)));
        Assert.True(summary.StepHeader);
        Assert.Equal(schema, summary.Schema);
        var rejection = IfcUploadService.ContentRejection(summary);
        Assert.Equal("IFC_UNSUPPORTED_SCHEMA", rejection?.Code);
        Assert.Equal(new[] { schema ?? "" }, rejection!.Details!["fileSchema"]);
    }

    [Fact]
    public void Schema_after_the_header_section_does_not_count()
    {
        var text = "ISO-10303-21;HEADER;FILE_NAME('a','b',(''),(''),'','','');ENDSEC;DATA;#1=IFCLABEL('FILE_SCHEMA((''IFC4''))');ENDSEC;END-ISO-10303-21;";
        Assert.Null(IfcStepScanner.Scan(Encoding.ASCII.GetBytes(text)).Schema);
    }

    [Fact]
    public void Non_step_content_is_an_unsupported_format_before_any_schema_check()
    {
        var zip = IfcStepScanner.Scan("PK\u0003\u0004FILE_SCHEMA(('IFC4'));"u8);
        Assert.False(zip.StepHeader);
        Assert.Null(zip.Schema);
        Assert.Equal("IFC_UNSUPPORTED_FORMAT", IfcUploadService.ContentRejection(zip)?.Code);
        Assert.Equal("IFC_UNSUPPORTED_FORMAT", IfcUploadService.ContentRejection(IfcStepScanner.Scan([]))?.Code);
    }

    [Fact]
    public void Chunked_input_reads_the_same_header_as_one_buffer()
    {
        var bytes = Encoding.ASCII.GetBytes(Model("FILE_SCHEMA(('IFC4X3_ADD2'));"));
        foreach (var size in new[] { 1, 3, 7, 64, 4096 })
        {
            var scanner = new IfcStepScanner();
            for (var i = 0; i < bytes.Length; i += size) scanner.Append(bytes.AsSpan(i, Math.Min(size, bytes.Length - i)));
            Assert.Equal(IfcStepScanner.Scan(bytes), scanner.Result());
        }
    }

    [Fact]
    public void Utf8_bom_before_the_header_is_accepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes(Model("FILE_SCHEMA(('IFC4'));"))).ToArray();
        Assert.Equal("IFC4", IfcStepScanner.Scan(bytes).Schema);
    }

    [Fact]
    public void Header_beyond_the_scanned_prefix_is_not_trusted()
    {
        var padding = new string(' ', IfcStepScanner.HeaderLimit);
        var summary = IfcStepScanner.Scan(Encoding.ASCII.GetBytes("ISO-10303-21;HEADER;" + padding + "FILE_SCHEMA(('IFC4'));ENDSEC;"));
        Assert.True(summary.StepHeader);
        Assert.Null(summary.Schema);
    }

    [Fact]
    public async Task Complete_rejects_an_unsupported_schema_through_the_store_and_never_claims_or_copies()
    {
        var bytes = Encoding.ASCII.GetBytes(Model("FILE_SCHEMA(('AP214'));"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = new FinalizeIfcUploadRequest("ifc/staging/model.ifc", bytes.Length, "application/octet-stream", hash, "model.ifc");
        var intent = new IfcUploadIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), input.ObjectKey, bytes.Length, hash, "model.ifc", DateTime.UtcNow.AddMinutes(10), false);
        IfcUploadRejection? rejected = null;
        Task<AuthResult<bool>> Reject(IfcUploadRejection value) { rejected = value; return Task.FromResult(AuthResult<bool>.Ok(true)); }
        var store = StubProxy.For<IIfcUploadStore>((method, args) => method switch
        {
            nameof(IIfcUploadStore.ReadAsync) => Task.FromResult(AuthResult<IfcUploadIntent>.Ok(intent)),
            nameof(IIfcUploadStore.RejectAsync) => Reject((IfcUploadRejection)args[3]!),
            _ => throw new Exception("Unexpected: " + method)
        });
        var inspector = StubProxy.For<IIfcSourceInspector>((method, _) => method switch
        {
            nameof(IIfcSourceInspector.MetadataAsync) => Task.FromResult<StorageObjectMetadata?>(new(bytes.Length, "application/octet-stream", "etag")),
            nameof(IIfcSourceInspector.InspectAsync) => Task.FromResult<IfcObjectDigest?>(new(bytes.Length, hash, "etag", IfcStepScanner.Scan(bytes))),
            _ => throw new Exception("Unexpected: " + method)
        });
        var service = new IfcUploadService(store, StubProxy.For<IStorageService>((m, _) => throw new Exception("Unexpected: " + m)), inspector,
            Options.Create(new IfcUploadOptions { Enabled = true, MaxBytes = 4096 }), TimeProvider.System);
        var result = await service.CompleteAsync(Guid.NewGuid(), intent.RevisionId, input, default);
        Assert.Equal(("IFC_UNSUPPORTED_SCHEMA", 422), (result.Error?.Code, result.Error?.Status));
        Assert.Equal("IFC_UNSUPPORTED_SCHEMA", rejected?.Code);
    }
}
