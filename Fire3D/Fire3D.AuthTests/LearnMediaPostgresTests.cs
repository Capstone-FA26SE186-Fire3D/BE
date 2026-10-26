using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#171: learn_media_images through the API's database login: idempotent intents, one audited completion, immutable afterwards,
    // private to Supabase's public roles; the HTTP surface needs PlatformAdmin except the public image address.
    [PostgresFact]
    public async Task Learn_media_images_are_idempotent_audited_immutable_and_private()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        using (var scope = factory!.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILearnMediaStore>();
            var now = DateTime.UtcNow; var id = Guid.NewGuid();
            var intent = new LearnImageRecord(id, adminId, new string('a', 64), "cover.png", "image/png", 68, new string('b', 64),
                LearnMediaRules.StagingKey(id), null, null, null, now, now.AddMinutes(15), null);
            Assert.Equal(id, (await store.CreateOrGetIntentAsync(intent, "media-1", default)).Id);
            var replay = await store.CreateOrGetIntentAsync(intent with { Id = Guid.NewGuid(), StagingKey = "learn/staging/other" }, "media-1", default);
            Assert.Equal(id, replay.Id);
            Assert.True(await store.CompleteAsync(id, LearnMediaRules.ImageKey(id), 1, 1, default));
            Assert.False(await store.CompleteAsync(id, "learn/images/other", 2, 2, default));
            var done = (await store.FindAsync(id, default))!;
            Assert.Equal((LearnMediaRules.ImageKey(id), 1, 1), (done.ObjectKey, done.Width, done.Height));
            Assert.NotNull(done.CompletedAt);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='LearnMediaImage' AND target_id='{id}' AND action='Create'"));
            // A completed image never changes, even for the table owner.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE learn_media_images SET file_name='x.png' WHERE id='{id}'"));
        }
        Assert.True((bool)(await ScalarAsync("SELECT relrowsecurity FROM pg_class WHERE oid='public.learn_media_images'::regclass"))!);
        Assert.True((bool)(await ScalarAsync("""
            SELECT bool_and(NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) OR NOT has_table_privilege(r,'public.learn_media_images','SELECT,INSERT,UPDATE,DELETE'))
            FROM unnest(ARRAY['anon','authenticated']) r
            """))!);
        Assert.False((bool)(await ScalarAsync("SELECT has_table_privilege('fire3d_api','public.learn_media_images','DELETE')"))!);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/learn/media/images/{Guid.NewGuid()}")).StatusCode);
        var body = new { fileName = "cover.png", contentType = "image/png", sizeBytes = 68, sha256 = new string('b', 64) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/learn/media/images/upload-intent", body)).StatusCode);
        var noKey = await client.PostAsJsonAsync("/api/admin/learn/media/images/upload-intent", body);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await noKey.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var gif = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/media/images/upload-intent") { Content = JsonContent.Create(body with { contentType = "image/gif" }) };
        gif.Headers.Add("Idempotency-Key", "gif");
        var rejected = await client.SendAsync(gif);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("VALIDATION_ERROR", (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/learn/media/images/{Guid.NewGuid()}/complete", null)).StatusCode);
    }
}
