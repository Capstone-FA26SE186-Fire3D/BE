using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;
using Xunit;

namespace Fire3D.AuthTests;

// Real controller, AvatarService and AvatarStore on a disposable database; only object storage is in memory.
public sealed class AvatarControllerHttpTests
{
    [BillingPostgresFact]
    public async Task Avatar_mutations_require_a_well_formed_if_match()
    {
        await using var db = await Database();
        var storage = new MemoryStorage();
        using var factory = Factory(db, storage);
        using var client = As(factory, BillingDatabase.Owner);
        foreach (var (method, route) in new[] { (HttpMethod.Post, "/api/me/avatar/complete"), (HttpMethod.Delete, "/api/me/avatar"), (HttpMethod.Post, "/api/me/avatar/upload") })
        {
            HttpContent? Body() => route.EndsWith("/upload", StringComparison.Ordinal) ? new MultipartFormDataContent { { new StringContent("x"), "other" } } : null;
            await Problem(await client.SendAsync(Request(method, route, null, Body())), HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED");
            await Problem(await client.SendAsync(Request(method, route, "abc", Body())), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
            await Problem(await client.SendAsync(Request(method, route, "\"0\"", Body())), HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        }
        Assert.Empty(storage.Objects);
        Assert.Equal(1L, await db.Scalar($"SELECT profile_revision_no FROM users WHERE id='{BillingDatabase.Owner}'"));
    }

    [BillingPostgresFact]
    public async Task Upload_intent_complete_get_and_delete_follow_the_profile_etag()
    {
        await using var db = await Database();
        var storage = new MemoryStorage();
        using var factory = Factory(db, storage);
        using var client = As(factory, BillingDatabase.Owner);
        await Problem(await client.GetAsync("/api/me/avatar"), HttpStatusCode.NotFound, "AVATAR_NOT_FOUND");
        await Problem(await client.PostAsJsonAsync("/api/me/avatar/upload-intent", new { contentType = "image/gif", contentLength = 10 }),
            HttpStatusCode.BadRequest, null);

        var png = Png();
        var intent = await client.PostAsJsonAsync("/api/me/avatar/upload-intent", new { contentType = "image/png", contentLength = png.Length });
        Assert.Equal(HttpStatusCode.OK, intent.StatusCode);
        var upload = (await intent.Content.ReadFromJsonAsync<AvatarUploadIntentResponse>())!;
        Assert.StartsWith("https://storage.test/upload/avatars/staging/", upload.UploadUrl);
        storage.Put(storage.LastPresignedKey!, png, "image/png"); // The browser PUT to the presigned URL.

        await Problem(await client.SendAsync(Request(HttpMethod.Post, "/api/me/avatar/complete", "\"1\"", JsonContent.Create(new { uploadId = Guid.NewGuid() }))),
            HttpStatusCode.Conflict, "INVALID_AVATAR_UPLOAD");
        var completed = await client.SendAsync(Request(HttpMethod.Post, "/api/me/avatar/complete", "\"1\"", JsonContent.Create(new { uploadId = upload.UploadId })));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("\"2\"", completed.Headers.ETag?.Tag);
        Assert.Equal(2, (await completed.Content.ReadFromJsonAsync<AvatarResponse>())!.ProfileRevision);
        var key = (string)(await db.Scalar($"SELECT avatar_storage_key FROM users WHERE id='{BillingDatabase.Owner}'"))!;
        Assert.StartsWith($"avatars/users/{BillingDatabase.Owner:N}/", key);
        Assert.True(storage.Objects.ContainsKey(key));

        var current = await client.GetAsync("/api/me/avatar");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("\"2\"", current.Headers.ETag?.Tag);
        Assert.Equal("https://storage.test/download/" + key, (await current.Content.ReadFromJsonAsync<AvatarResponse>())!.Url);
        Assert.True(current.Headers.CacheControl?.NoStore);

        await Problem(await client.SendAsync(Request(HttpMethod.Delete, "/api/me/avatar", "\"1\"")), HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
        var deleted = await client.SendAsync(Request(HttpMethod.Delete, "/api/me/avatar", "\"2\""));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("\"3\"", deleted.Headers.ETag?.Tag);
        Assert.Equal(true, await db.Scalar($"SELECT avatar_storage_key IS NULL AND profile_revision_no=3 FROM users WHERE id='{BillingDatabase.Owner}'"));
        await Problem(await client.GetAsync("/api/me/avatar"), HttpStatusCode.NotFound, "AVATAR_NOT_FOUND");
    }

    [BillingPostgresFact]
    public async Task Multipart_upload_stores_the_file_and_returns_the_next_etag()
    {
        await using var db = await Database();
        var storage = new MemoryStorage();
        using var factory = Factory(db, storage);
        using var client = As(factory, BillingDatabase.Owner);

        using var noFile = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        await Problem(await client.SendAsync(Request(HttpMethod.Post, "/api/me/avatar/upload", "\"1\"", noFile)), HttpStatusCode.BadRequest, "VALIDATION_ERROR");

        var png = Png();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "avatar.png");
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/me/avatar/upload", "\"1\"", form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        Assert.Single(storage.Objects.Keys, k => k.StartsWith("avatars/staging/", StringComparison.Ordinal));
        Assert.Equal(png, storage.Objects[(string)(await db.Scalar($"SELECT avatar_storage_key FROM users WHERE id='{BillingDatabase.Owner}'"))!].Bytes);

        using var stale = new MultipartFormDataContent();
        var again = new ByteArrayContent(png);
        again.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        stale.Add(again, "file", "avatar.png");
        await Problem(await client.SendAsync(Request(HttpMethod.Post, "/api/me/avatar/upload", "\"1\"", stale)), HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED");
    }

    private static async Task<BillingDatabase> Database()
    {
        var db = await BillingDatabase.Create(false);
        await BackendDatabasePermissionsTests.Apply(db, "AddAvatarCleanupGate");
        return db;
    }

    private static WebApplicationFactory<Program> Factory(BillingDatabase db, MemoryStorage storage) =>
        new BillingApiTests.Factory(db).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStorageService>();
            services.AddSingleton<IStorageService>(storage);
        }));

    private static HttpClient As(WebApplicationFactory<Program> factory, Guid actor)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Actor", actor.ToString()); return client;
    }

    private static HttpRequestMessage Request(HttpMethod method, string route, string? ifMatch, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, route) { Content = content ?? (method == HttpMethod.Post ? JsonContent.Create(new { uploadId = Guid.NewGuid() }) : null) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        if (code is not null) Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal sealed class MemoryStorage : IStorageService
    {
        public ConcurrentDictionary<string, (byte[] Bytes, string Type)> Objects { get; } = new();
        public string? LastPresignedKey { get; private set; }
        public void Put(string key, byte[] bytes, string type) => Objects[key] = (bytes, type);
        private static string Etag(byte[] bytes) => "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"";
        public async Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            Put(objectKey, copy.ToArray(), contentType);
        }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct)
        { LastPresignedKey = objectKey; return Task.FromResult("https://storage.test/upload/" + objectKey); }
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(objectKey, out var value) && value.Bytes.Length == expectedSizeBytes);
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(objectKey, out var value) ? new StorageObjectMetadata(value.Bytes.Length, value.Type, Etag(value.Bytes)) : null);
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(objectKey, out var value) && Etag(value.Bytes) == expectedETag ? value.Bytes.Take(length).ToArray() : null);
        public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(objectKey, out var value) && Etag(value.Bytes) == expectedETag && value.Bytes.Length <= maxBytes ? value.Bytes : null);
        public Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct)
        {
            if (!Objects.TryGetValue(sourceKey, out var value) || Etag(value.Bytes) != sourceETag) return Task.FromResult(false);
            Put(destinationKey, value.Bytes, contentType);
            return Task.FromResult(true);
        }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct) { Objects.TryRemove(objectKey, out _); return Task.CompletedTask; }
        public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) =>
            Task.FromResult("https://storage.test/download/" + objectKey);
    }
}
