using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class IfcSourceInspectorTests
{
    private static IfcSourceInspector Inspector(IAmazonS3 client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AWS:BucketName"]="ifc-test-private" }).Build());

    [Fact]
    public async Task Digest_pins_etag_and_hashes_a_fragmented_stream()
    {
        var bytes=Enumerable.Range(0,500).Select(x=>(byte)x).ToArray();
        var client=ResetProxy.For<IAmazonS3>((method,args)=>
        {
            Assert.Equal("GetObjectAsync",method); Assert.Equal("inspected",Assert.IsType<GetObjectRequest>(args![0]).EtagToMatch);
            return Task.FromResult(new GetObjectResponse { ContentLength=bytes.Length,ETag="inspected",ResponseStream=new Fragmented(bytes) });
        });
        var digest=await Inspector(client).InspectAsync("source","inspected",bytes.Length,default);
        Assert.Equal(bytes.Length,digest!.Size); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),digest.Hash);
    }
    [Fact]
    public async Task Digest_rejects_oversize_stream_even_if_the_provider_lies_about_length()
    {
        var client=ResetProxy.For<IAmazonS3>((_,_)=>Task.FromResult(new GetObjectResponse { ContentLength=1,ETag="inspected",ResponseStream=new Fragmented(new byte[1000]) }));
        Assert.Null(await Inspector(client).InspectAsync("source","inspected",12,default));
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task Source_missing_or_changed_is_not_accepted(HttpStatusCode status)
    {
        var client=ResetProxy.For<IAmazonS3>((_,_)=>throw new AmazonS3Exception("fake") { StatusCode=status });
        Assert.Null(await Inspector(client).InspectAsync("source","inspected",12,default));
        Assert.False(await Inspector(client).CopyAsync("source","inspected","candidate",default));
    }
    [Fact]
    public async Task Conditional_copy_pins_inspected_source_and_preserves_provider_failure_kind()
    {
        var client=ResetProxy.For<IAmazonS3>((_,args)=>
        {
            var request=Assert.IsType<CopyObjectRequest>(args![0]);
            Assert.Equal("inspected",request.ETagToMatch); Assert.Equal("candidate",request.DestinationKey);
            Assert.Equal(S3MetadataDirective.REPLACE,request.MetadataDirective);
            throw new AmazonS3Exception("fake") { StatusCode=HttpStatusCode.ServiceUnavailable };
        });
        await Assert.ThrowsAsync<IfcStorageFailure>(()=>Inspector(client).CopyAsync("source","inspected","candidate",default));
    }
    [Fact]
    public async Task Metadata_maps_provider_headers_and_treats_only_missing_objects_as_absent()
    {
        var client=ResetProxy.For<IAmazonS3>((method,args)=>
        {
            Assert.Equal("GetObjectMetadataAsync",method);
            var request=Assert.IsType<GetObjectMetadataRequest>(args![0]);
            Assert.Equal(("ifc-test-private","source"),(request.BucketName,request.Key));
            var response=new GetObjectMetadataResponse { ContentLength=77,ETag="inspected" }; response.Headers.ContentType="application/octet-stream";
            return Task.FromResult(response);
        });
        Assert.Equal(new Fire3D.Application.Storage.StorageObjectMetadata(77,"application/octet-stream","inspected"),await Inspector(client).MetadataAsync("source",default));
        var missing=ResetProxy.For<IAmazonS3>((_,_)=>throw new AmazonS3Exception("fake") { StatusCode=HttpStatusCode.NotFound });
        Assert.Null(await Inspector(missing).MetadataAsync("source",default));
        var unavailable=ResetProxy.For<IAmazonS3>((_,_)=>throw new AmazonS3Exception("fake") { StatusCode=HttpStatusCode.ServiceUnavailable });
        await Assert.ThrowsAsync<IfcStorageFailure>(()=>Inspector(unavailable).MetadataAsync("source",default));
    }
    [Fact]
    public async Task Conditional_copy_reports_success_and_digest_wraps_unexpected_provider_failures()
    {
        var copied=ResetProxy.For<IAmazonS3>((_,args)=>
        {
            Assert.Equal("application/octet-stream",Assert.IsType<CopyObjectRequest>(args![0]).ContentType);
            return Task.FromResult(new CopyObjectResponse());
        });
        Assert.True(await Inspector(copied).CopyAsync("source","inspected","candidate",default));
        var failing=ResetProxy.For<IAmazonS3>((_,_)=>throw new AmazonS3Exception("fake") { StatusCode=HttpStatusCode.Forbidden });
        await Assert.ThrowsAsync<IfcStorageFailure>(()=>Inspector(failing).InspectAsync("source","inspected",12,default));
    }
    private sealed class Fragmented(byte[] data):MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) => base.ReadAsync(buffer[..Math.Min(buffer.Length,3)],ct);
    }
}
