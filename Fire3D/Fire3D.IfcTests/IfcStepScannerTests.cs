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
        $"ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\r\nFILE_NAME('a.ifc','2026-11-23',(''),(''),'','','');\r\n{schemaClause}\r\nENDSEC;\r\nDATA;\r\n#1=IFCPROJECT('x',$,'p',$,$,$,$,$,$);\r\n#2=IFCBUILDINGSTOREY('a',$,'L1',$,$,$,$,$,.ELEMENT.,0.);\r\n#3=IFCSPACE('b',$,'101',$,$,$,$,$,.ELEMENT.,.INTERNAL.,$);\r\n#4=IFCDOOR('c',$,'D1',$,$,$,$,$,2.1,0.9,$,$,$);\r\n#5=IFCWALLSTANDARDCASE('d',$,'W1',$,$,$,$,$,$);\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n";

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

    // #219 precheck counts entity instances while streaming.
    private static IfcEntityCounts Counts(string data) => IfcStepScanner.Scan(Encoding.ASCII.GetBytes(
        "ISO-10303-21;HEADER;FILE_SCHEMA(('IFC4'));ENDSEC;DATA;" + data + "ENDSEC;END-ISO-10303-21;")).Counts;

    [Fact]
    public void Precheck_counts_entity_instances_only()
    {
        var counts = Counts(
            "#1=IFCBUILDINGSTOREY('a',$,'L1',$,$,$,$,$,.ELEMENT.,0.);\n#2 = ifcBuildingStorey ('b');\n" +
            "#3=IFCSPACE('c',$,'IFCDOOR(',$,$);\n#4=IFCSPACETYPE('t');\n" +
            "#5=IFCDOORSTANDARDCASE('d');#6=IFCDOOR('e');#7=IFCDOORTYPE('f');\n" +
            "#8=IFCWALL('g');#9=IFCWALLSTANDARDCASE('h');#10=IFCWALLELEMENTEDCASE('i');#11=IFCWALLTYPE('j');#12=IFCSLAB('k');#13=IFCSLABTYPE('l');\n" +
            "#14=IFCPROPERTYSINGLEVALUE('Note',$,IFCLABEL('#99=IFCWALL(x)'),$);\n/* #15=IFCSPACE('commented'); */\n" +
            "#16=IFCLABEL('it''s #17=IFCDOOR(');\n#18=IFCRELAGGREGATES('m',$,$,$,#1,(#3));");
        Assert.Equal(new IfcEntityCounts(2, 1, 2, 4), counts);
        Assert.Empty(counts.Missing());
    }

    [Theory]
    [InlineData("#2=IFCSPACE('b');#3=IFCDOOR('c');#4=IFCSLAB('d');", "IfcBuildingStorey", "IFC_NO_STOREYS")]
    [InlineData("#1=IFCBUILDINGSTOREY('a');#3=IFCDOOR('c');#4=IFCWALL('d');", "IfcSpace", "IFC_NO_SPACES")]
    [InlineData("#1=IFCBUILDINGSTOREY('a');#2=IFCSPACE('b');#4=IFCWALL('d');", "IfcDoor", "IFC_NO_DOORS")]
    [InlineData("#1=IFCBUILDINGSTOREY('a');#2=IFCSPACE('b');#3=IFCDOOR('c');#4=IFCWALLTYPE('d');", "IfcWall/IfcSlab", "IFC_NO_WALLS_OR_SLABS")]
    public void Precheck_names_each_missing_group_with_its_qa_code(string data, string group, string code)
    {
        var bytes = Encoding.ASCII.GetBytes("ISO-10303-21;HEADER;FILE_SCHEMA(('IFC2X3'));ENDSEC;DATA;" + data + "ENDSEC;END-ISO-10303-21;");
        var rejection = IfcUploadService.ContentRejection(IfcStepScanner.Scan(bytes));
        Assert.Equal("IFC_PRECHECK_FAILED", rejection?.Code);
        Assert.Equal(new[] { group }, rejection!.Details!["missing"]);
        Assert.Equal(new[] { code }, rejection.Details["qaCodes"]);
    }

    [Fact]
    public void Precheck_of_an_empty_data_section_lists_every_group()
    {
        var rejection = IfcUploadService.ContentRejection(IfcStepScanner.Scan("ISO-10303-21;HEADER;FILE_SCHEMA(('IFC4'));ENDSEC;DATA;ENDSEC;END-ISO-10303-21;"u8));
        Assert.Equal(new[] { "IFC_NO_STOREYS", "IFC_NO_SPACES", "IFC_NO_DOORS", "IFC_NO_WALLS_OR_SLABS" }, rejection!.Details!["qaCodes"]);
    }

    [Fact]
    public void Schema_is_checked_before_the_precheck()
    {
        var rejection = IfcUploadService.ContentRejection(IfcStepScanner.Scan("ISO-10303-21;HEADER;FILE_SCHEMA(('AP214'));ENDSEC;DATA;ENDSEC;"u8));
        Assert.Equal("IFC_UNSUPPORTED_SCHEMA", rejection?.Code);
    }

    [Fact]
    public void Precheck_streams_a_large_model_in_small_chunks()
    {
        // About 40 MB of walls fed 64 KiB at a time: counts stay exact across chunk borders; only the header is buffered.
        var scanner = new IfcStepScanner();
        scanner.Append("ISO-10303-21;HEADER;FILE_SCHEMA(('IFC4'));ENDSEC;DATA;#1=IFCBUILDINGSTOREY('a');#2=IFCSPACE('b');#3=IFCDOOR('c');"u8);
        var line = Encoding.ASCII.GetBytes("#10=IFCWALLSTANDARDCASE('2O2Fr$t4X7Zf8NOew3FLOH',$,'Basic Wall',$,$,#20,#30,$,$);\r\n");
        var chunk = new byte[64 * 1024]; var filled = 0;
        const int walls = 500_000;
        for (var i = 0; i < walls; i++)
            foreach (var b in line)
            {
                chunk[filled++] = b;
                if (filled == chunk.Length) { scanner.Append(chunk); filled = 0; }
            }
        scanner.Append(chunk.AsSpan(0, filled));
        scanner.Append("ENDSEC;END-ISO-10303-21;"u8);
        var result = scanner.Result();
        Assert.Equal(new IfcEntityCounts(1, 1, 1, walls), result.Counts);
        Assert.Equal("IFC4", result.Schema);
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
