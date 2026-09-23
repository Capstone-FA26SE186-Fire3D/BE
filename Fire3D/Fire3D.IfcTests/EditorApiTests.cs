using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class EditorApiTests
{
    [Theory]
    [InlineData(null,428)]
    [InlineData("*",400)]
    [InlineData("W/\"0\"",400)]
    [InlineData("\"-1\"",400)]
    public async Task Write_requires_strong_version_precondition(string? tag,int status)
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IAnnotationStore>((_,_)=>throw new Exception("Must not write"));
        var result=await new SaveAnnotationsHandler(RevisionAccessTests.Accounts(actor),store)
            .Handle(new(actor.Id,Guid.NewGuid(),tag,new([])),default);
        Assert.Equal(status,result.Error?.Status);
    }
    [Fact]
    public async Task Trainee_cannot_read_preview_or_write_annotations()
    {
        var actor=RevisionAccessTests.Actor(UserRole.Trainee);
        var accounts=RevisionAccessTests.Accounts(actor);
        var store=StubProxy.For<IAnnotationStore>((_,_)=>throw new Exception("Must not write"));
        Assert.Equal(403,(await new SaveAnnotationsHandler(accounts,store)
            .Handle(new(actor.Id,Guid.NewGuid(),"\"0\"",new([])),default)).Error?.Status);
        var previews=StubProxy.For<IEditorPreviewStore>((_,_)=>throw new Exception("Must not read"));
        var signer=StubProxy.For<IPreviewDownloadSigner>((_,_)=>throw new Exception("Must not sign"));
        Assert.Equal(403,(await new GetEditorPreviewHandler(accounts,previews,signer)
            .Handle(new(actor.Id,Guid.NewGuid(),Guid.NewGuid()),default)).Error?.Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_complete_metadata_is_signed(bool complete)
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var source=new EditorPreviewSource(Guid.NewGuid(),Guid.NewGuid(),"Processing",Guid.NewGuid(),Guid.NewGuid(),
            "private/key",new string('a',64),JsonSerializer.SerializeToElement(complete?new int[16]:new int[3]),
            JsonSerializer.SerializeToElement(Array.Empty<object>()),JsonSerializer.SerializeToElement(new {}));
        var store=StubProxy.For<IEditorPreviewStore>((_,args)=> {
            Assert.Equal(actor.OrganizationId,args[2]); return Task.FromResult<EditorPreviewSource?>(source);
        });
        var calls=0;
        var signer=StubProxy.For<IPreviewDownloadSigner>((_,_)=> {calls++;return Task.FromResult(new SignedDownload("https://example.test/signed",DateTimeOffset.UtcNow.AddMinutes(5)));});
        var result=await new GetEditorPreviewHandler(RevisionAccessTests.Accounts(actor),store,signer)
            .Handle(new(actor.Id,source.BuildingId,source.RevisionId),default);
        Assert.Equal(complete?"Ready":"NotReady",result.Value?.Status);
        Assert.Equal(complete?1:0,calls);
    }
}

public sealed class EditorApiSqlTests(IfcReadDatabase database) : IClassFixture<IfcReadDatabase>
{
    [IfcPostgresFact]
    public async Task Preview_provenance_and_annotation_concurrency_are_enforced_in_postgres()
    {
        await using var db=database.Context();
        var actor=Guid.NewGuid(); var other=Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE revision_artifacts ADD COLUMN storage_key text;
            CREATE TABLE users(id uuid primary key,organization_id uuid,role text,is_active boolean,deleted_at timestamptz);
            CREATE TABLE annotation_sets(id uuid primary key,revision_id uuid REFERENCES revisions(id),version_number int NOT NULL,
                data jsonb NOT NULL,provenance varchar(50),created_by uuid,created_at timestamptz DEFAULT now(),UNIQUE(revision_id,version_number));
            CREATE TABLE audit_logs(id uuid DEFAULT gen_random_uuid(),user_id uuid,organization_id uuid,actor_type text,
                action text,target_entity text,target_id uuid,new_values jsonb);
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users VALUES ({actor},{database.Tenant},'OrganizationUser',true,null),({other},{database.OtherTenant},'OrganizationUser',true,null)");
        var preview=new EditorPreviewStore(db);
        Assert.Null((await preview.ReadAsync(database.Building,database.Revision,database.Tenant,default))!.ArtifactId);
        Assert.Null(await preview.ReadAsync(database.Building,database.Revision,database.OtherTenant,default));
        Assert.Null(await preview.ReadAsync(Guid.NewGuid(),database.Revision,database.Tenant,default));
        await db.Database.ExecuteSqlRawAsync("UPDATE processing_jobs SET status='Succeeded'; UPDATE processing_job_attempts SET status='Succeeded'; UPDATE revision_artifacts SET storage_key='private/preview.glb'");
        Assert.Equal(database.Artifact,(await preview.ReadAsync(database.Building,database.Revision,database.Tenant,default))!.ArtifactId);
        await db.Database.ExecuteSqlRawAsync("UPDATE processing_job_attempts SET input_hash='mismatch'");
        Assert.Null((await preview.ReadAsync(database.Building,database.Revision,database.Tenant,default))!.ArtifactId);

        var store=new AnnotationStore(db);
        Assert.Equal("\"0\"",(await store.ReadAsync(database.Revision,database.Tenant,default))!.ETag);
        Assert.Null(await store.ReadAsync(database.Revision,database.OtherTenant,default));
        var data=new AnnotationData([new(Guid.NewGuid(),"IFC-SPACE-1","Room",null)]);
        Assert.Equal(404,(await store.AppendAsync(other,database.Revision,0,data,default)).Error?.Status);
        Assert.Equal(400,(await store.AppendAsync(actor,database.Revision,0,new([new(Guid.NewGuid(),"foreign-anchor","Room",null)]),default)).Error?.Status);
        await using var db2=database.Context();
        var outcomes=await Task.WhenAll(store.AppendAsync(actor,database.Revision,0,data,default),
            new AnnotationStore(db2).AppendAsync(actor,database.Revision,0,data,default));
        Assert.Single(outcomes,x=>x.IsSuccess);
        Assert.Equal(412,Assert.Single(outcomes,x=>!x.IsSuccess).Error!.Status);
        Assert.Equal(1,(await store.ReadAsync(database.Revision,database.Tenant,default))!.Version);
        // Audit failure must roll back the newly appended version.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_logs ADD CONSTRAINT reject_audit CHECK (false) NOT VALID");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>store.AppendAsync(actor,database.Revision,1,data,default));
        Assert.Equal(1,(await store.ReadAsync(database.Revision,database.Tenant,default))!.Version);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_logs DROP CONSTRAINT reject_audit");
        Assert.Equal(2,(await store.AppendAsync(actor,database.Revision,1,new([]),default)).Value!.Version);
        Assert.Equal(2,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM annotation_sets").SingleAsync());
        Assert.Equal(2,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM audit_logs").SingleAsync());
    }
}
