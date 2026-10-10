using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class PreviewStorageTests
{
    private static IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AWS:BucketName"]="private-test-bucket" }).Build();

    [Theory]
    [InlineData(100,true)]
    [InlineData(99,false)]
    public async Task Signing_requires_head_size_and_preserves_server_bucket_key(long actualSize,bool ready)
    {
        var signed=0;
        var client=StubProxy.For<IAmazonS3>((method,args)=>
        {
            if(method=="GetObjectMetadataAsync")
            {
                var request=Assert.IsType<GetObjectMetadataRequest>(args[0]);
                Assert.Equal("private-test-bucket",request.BucketName);Assert.Equal("server/preview.glb",request.Key);
                return Task.FromResult(new GetObjectMetadataResponse { ContentLength=actualSize,ETag="not-a-sha256" });
            }
            var url=Assert.IsType<GetPreSignedUrlRequest>(args[0]);signed++;
            Assert.Equal(HttpVerb.GET,url.Verb);Assert.InRange(url.Expires!.Value-DateTime.UtcNow,TimeSpan.FromMinutes(4.9),TimeSpan.FromMinutes(5.1));
            return Task.FromResult("https://example.test/signed");
        });
        var result=await new S3PreviewDownloadSigner(client,Configuration).SignAsync("server/preview.glb",100,default);
        Assert.Equal(ready,result is not null);Assert.Equal(ready?1:0,signed);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Missing_is_not_ready_but_storage_failures_are_unavailable(HttpStatusCode status)
    {
        var client=StubProxy.For<IAmazonS3>((method,_)=>
        {
            Assert.Equal("GetObjectMetadataAsync",method);
            return Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("Provider details must not leak") { StatusCode=status });
        });
        var signer=new S3PreviewDownloadSigner(client,Configuration);
        if(status==HttpStatusCode.NotFound)Assert.Null(await signer.SignAsync("server/preview.glb",100,default));
        else await Assert.ThrowsAsync<PreviewStorageUnavailableException>(()=>signer.SignAsync("server/preview.glb",100,default));
    }

    [Fact]
    public async Task Provider_timeout_is_unavailable_and_request_cancellation_is_preserved()
    {
        var client=StubProxy.For<IAmazonS3>((_,_)=>Task.FromException<GetObjectMetadataResponse>(new OperationCanceledException()));
        await Assert.ThrowsAsync<PreviewStorageUnavailableException>(()=>new S3PreviewDownloadSigner(client,Configuration).SignAsync("server/key",100,default));
        using var cancellation=new CancellationTokenSource();
        var cancelled=StubProxy.For<IAmazonS3>((_,_)=>{cancellation.Cancel();return Task.FromCanceled<GetObjectMetadataResponse>(cancellation.Token);});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new S3PreviewDownloadSigner(cancelled,Configuration).SignAsync("server/key",100,cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_never_emits_a_url_for_missing_or_unavailable_objects(bool unavailable)
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var source=new EditorPreviewSource(Guid.NewGuid(),Guid.NewGuid(),"Processed",Guid.NewGuid(),Guid.NewGuid(),"server/key",new string('a',64),
            JsonSerializer.SerializeToElement(new int[16]),JsonSerializer.SerializeToElement(Array.Empty<object>()),JsonSerializer.SerializeToElement(new{}),100);
        var store=StubProxy.For<IEditorPreviewStore>((_,_)=>Task.FromResult<EditorPreviewSource?>(source));
        var signer=StubProxy.For<IPreviewDownloadSigner>((_,_)=>unavailable
            ? Task.FromException<SignedDownload?>(new PreviewStorageUnavailableException(new Exception("secret provider payload")))
            : Task.FromResult<SignedDownload?>(null));
        var result=await new GetEditorPreviewHandler(RevisionAccessTests.Accounts(actor),store,signer).Handle(new(actor.Id,source.BuildingId,source.RevisionId),default);
        if(unavailable){Assert.Equal(503,result.Error?.Status);Assert.Equal("PREVIEW_STORAGE_UNAVAILABLE",result.Error?.Code);}
        else{Assert.Equal("NotReady",result.Value?.Status);Assert.Null(result.Value?.DownloadUrl);}
    }
}
