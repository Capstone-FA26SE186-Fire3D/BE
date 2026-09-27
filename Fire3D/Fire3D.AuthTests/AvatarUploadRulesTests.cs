using Fire3D.Application.Authentication.Avatar;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarUploadRulesTests
{
    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void Upload_intent_accepts_supported_image_content_types(string contentType)
    {
        Assert.Null(AvatarUploadRules.ValidateIntent(contentType, 5 * 1024 * 1024));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    [InlineData("")]
    public void Upload_intent_rejects_unsupported_content_type(string contentType)
    {
        Assert.Equal("INVALID_AVATAR_MEDIA_TYPE", AvatarUploadRules.ValidateIntent(contentType, 1)?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5 * 1024 * 1024 + 1)]
    public void Upload_intent_rejects_an_empty_or_oversize_file(long contentLength)
    {
        Assert.Equal("INVALID_AVATAR_SIZE", AvatarUploadRules.ValidateIntent("image/png", contentLength)?.Code);
    }
}
