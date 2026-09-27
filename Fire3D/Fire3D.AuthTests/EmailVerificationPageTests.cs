using Xunit;

namespace Fire3D.AuthTests;

public sealed class EmailVerificationPageTests
{
    [Theory]
    [InlineData("verify-email", "app.js")]
    [InlineData("check-email", "app.js")]
    public void Public_email_pages_load_assets_without_discarding_a_path_base(string page, string asset)
    {
        var repository = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(repository, "Fire3D.API", "wwwroot", page, "index.html"));

        Assert.Contains($"src=\"{asset}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"src=\"/{page}/", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_page_links_to_resend_without_discarding_a_path_base()
    {
        var repository = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(repository, "Fire3D.API", "wwwroot", "verify-email", "index.html"));

        Assert.Contains("href=\"../check-email/\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/check-email/\"", html, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fire3D.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate Fire3D.sln from the test working directory.");
    }
}
