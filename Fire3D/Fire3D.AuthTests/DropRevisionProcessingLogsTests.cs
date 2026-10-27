using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // DropRevisionProcessingLogs: the writer-less log table and the two enums only it used are gone; processing logs read attempts.
    [PostgresFact]
    public async Task Revision_processing_logs_and_their_enums_are_dropped()
    {
        Assert.Equal(true, await ScalarAsync("SELECT to_regclass('public.revision_processing_logs') IS NULL"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM pg_type WHERE typnamespace='public'::regnamespace AND typname IN('processing_step_enum','processing_step_status_enum')"));
        Assert.Equal(true, await ScalarAsync("SELECT to_regclass('public.processing_job_attempts') IS NOT NULL"));
    }
}
