using System.Text.Json;
using Fire3D.Infrastructure.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class FirebaseSignInProviderTests
{
    [Theory]
    [InlineData("\"google.com\"", true)]
    [InlineData("[\"google.com\"]", true)]
    [InlineData("[\"password\", \"google.com\"]", true)]
    [InlineData("\"password\"", false)]
    [InlineData("[\"password\"]", false)]
    public void Recognizes_google_provider_in_string_and_array_claims(string rawProvider, bool expected)
    {
        using var document = JsonDocument.Parse(rawProvider);

        Assert.Equal(expected, FirebaseSignInProvider.IsGoogle(document.RootElement));
    }
}
