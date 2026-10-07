using Xunit;
using Fire3D.Infrastructure.Ifc;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.IfcTests;
public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task Running_or_failed_current_attempt_cannot_expose_worker_outputs()
    {
        await using var db=database.Context();var store=new IfcReadStore(db);
        try
        {
            foreach(var status in new[]{"Running","Failed"})
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE processing_job_attempts SET status={status} WHERE id={database.Attempt}");
                Assert.Null(await store.GetValidationAsync(database.Validation,database.Tenant,default));
                Assert.Empty((await store.GetJobQaAsync(database.Job,database.Tenant,1,20,default))!.ValidationRuns.Items);
                Assert.Empty((await store.ListRevisionArtifactsAsync(database.Revision,database.Tenant,1,20,default))!.Items);
                Assert.Empty((await store.ListRevisionIssuesAsync(database.Revision,database.Tenant,1,20,default))!.Items);
            }
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE processing_job_attempts SET status='Succeeded' WHERE id={database.Attempt}");
        }
        Assert.NotNull(await store.GetValidationAsync(database.Validation,database.Tenant,default));
        Assert.Single((await store.ListRevisionArtifactsAsync(database.Revision,database.Tenant,1,20,default))!.Items);
    }
}
