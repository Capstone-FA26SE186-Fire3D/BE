using Fire3D.Application.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PasswordAndEmailConfigurationTests
{
    [Theory]
    [InlineData("12345", false)]
    [InlineData("123456", true)]
    [InlineData("      ", false)]
    public void Password_policy_accepts_six_characters_and_rejects_short_or_whitespace_values(string password, bool expected)
    {
        Assert.Equal(expected, PasswordResetValidation.ValidPassword(password));
    }

    [Fact]
    public void Password_policy_accepts_128_and_rejects_129_characters()
    {
        Assert.True(PasswordResetValidation.ValidPassword(new string('a', 128)));
        Assert.False(PasswordResetValidation.ValidPassword(new string('a', 129)));
    }

    [Fact]
    public void Production_email_configuration_requires_https_and_development_allows_loopback()
    {
        var production = new AuthEmailOptions { FrontendUrl = "http://localhost:3000", VerificationUrl = "http://localhost:3000" };
        var development = new AuthEmailOptions { FrontendUrl = "http://localhost:3000", VerificationUrl = "http://localhost:3000" };

        Assert.False(production.IsValidForEnvironment(allowLoopbackHttp: false));
        Assert.True(development.IsValidForEnvironment(allowLoopbackHttp: true));
    }

    [Fact]
    public void FET3D_email_origins_build_the_expected_links()
    {
        var options = new AuthEmailOptions
        {
            FrontendUrl = "https://fet3d.io.vn",
            VerificationUrl = "https://fet3d.io.vn"
        };

        Assert.True(options.IsValidForEnvironment(allowLoopbackHttp: false));
        Assert.Equal("https://fet3d.io.vn", options.GetFrontendPageBaseUrl());
        Assert.Equal("https://fet3d.io.vn", options.GetVerificationPageBaseUrl());
    }

    [Fact]
    public void Refresh_token_cleanup_has_safe_defaults()
    {
        var optionsType = typeof(AuthEmailOptions).Assembly.GetType(
            "Fire3D.Application.Authentication.AuthTokenCleanupOptions");

        Assert.NotNull(optionsType);
        var options = Activator.CreateInstance(optionsType!);
        Assert.True((bool)optionsType.GetProperty("Enabled")!.GetValue(options)!);
        Assert.Equal(60, (int)optionsType.GetProperty("IntervalMinutes")!.GetValue(options)!);
        Assert.Equal(7, (int)optionsType.GetProperty("RetentionDays")!.GetValue(options)!);
        Assert.Equal(500, (int)optionsType.GetProperty("BatchSize")!.GetValue(options)!);
        Assert.Equal(10, (int)optionsType.GetProperty("MaxBatchesPerRun")!.GetValue(options)!);
    }
}
