using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Tests.Shared;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<(Guid Item, Guid Version)> LibraryVersion(string kind, string code, JsonObject payload, bool publish)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/library/items") { Content = JsonContent.Create(new { kind, code }) };
        create.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var item = await client.SendAsync(create);
        Assert.True(item.StatusCode == HttpStatusCode.Created, await item.Content.ReadAsStringAsync());
        var itemId = (await item.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var add = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/library/items/{itemId}/versions") { Content = JsonContent.Create(new { name = code, payload }) };
        add.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var version = await client.SendAsync(add);
        Assert.True(version.StatusCode == HttpStatusCode.Created, await version.Content.ReadAsStringAsync());
        var versionId = (await version.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        if (publish)
        {
            var tag = (await client.GetAsync($"/api/admin/library/versions/{versionId}")).Headers.ETag!.Tag;
            var post = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/library/versions/{versionId}/publish");
            post.Headers.TryAddWithoutValidation("If-Match", tag);
            var published = await client.SendAsync(post);
            Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        }
        return (itemId, versionId);
    }

    private async Task<HttpResponseMessage> PutStyle(HttpClient http, Guid revision, object body, string? ifMatch)
    {
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/revisions/{revision}/style") { Content = JsonContent.Create(body) };
        if (ifMatch is not null) put.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await http.SendAsync(put);
    }

    // STYLE-01: presets and schemes are Library kinds; a revision keeps an append-only, ETag'd selection of them.
    [PostgresFact]
    public async Task Revision_style_selects_published_presets_with_etags_and_suggests_it_on_the_next_revision()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var samples = RevisionStyleUnitTests.Samples();
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var preset = await LibraryVersion("StylePreset", "SCHOOL-" + suffix, samples["StylePreset"]!["SCHOOL"]!.AsObject(), publish: true);
        var scheme = await LibraryVersion("ColorScheme", "WARM-" + suffix, samples["ColorScheme"]!["WARM-NEUTRAL"]!.AsObject(), publish: true);
        var draft = await LibraryVersion("StylePreset", "DRAFT-" + suffix, samples["StylePreset"]!["OFFICE"]!.AsObject(), publish: false);

        var revision = await SeedVerifiedIfcRevision();
        var empty = await client.GetAsync($"/api/revisions/{revision}/style");
        Assert.Equal(("\"0\"", HttpStatusCode.OK), (empty.Headers.ETag!.Tag, empty.StatusCode));
        Assert.Null((await empty.Content.ReadFromJsonAsync<JsonObject>())!["selection"]);

        var body = new { presetVersionId = preset.Version, schemeVersionId = scheme.Version,
            overrides = new[] { new { room = "wc", surface = "floor", material = "floor.tile" }, new { room = "default", surface = "wall", material = "wall.brick" } } };
        Assert.Equal((HttpStatusCode)428, (await PutStyle(client, revision, body, null)).StatusCode);
        var unpublished = await PutStyle(client, revision, new { presetVersionId = draft.Version }, "\"0\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unpublished.StatusCode);
        Assert.Contains("STYLE_SELECTION_INVALID", await unpublished.Content.ReadAsStringAsync());
        Assert.Contains("STYLE_SELECTION_INVALID", await (await PutStyle(client, revision, new { presetVersionId = scheme.Version }, "\"0\"")).Content.ReadAsStringAsync());
        var duplicate = await PutStyle(client, revision, new { presetVersionId = preset.Version,
            overrides = new[] { new { room = "wc", surface = "floor", material = "floor.tile" }, new { room = "wc", surface = "floor", material = "floor.vinyl" } } }, "\"0\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("CONTENT_INVALID", await duplicate.Content.ReadAsStringAsync());

        var saved = await PutStyle(client, revision, body, "\"0\"");
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        Assert.Equal("\"1\"", saved.Headers.ETag!.Tag);
        var view = (await saved.Content.ReadFromJsonAsync<RevisionStyleView>(Json))!;
        Assert.Equal(("SCHOOL-" + suffix, "WARM-" + suffix, 2), (view.Selection!.Preset!.Code, view.Selection.Scheme!.Code, view.Selection.Overrides.Count));
        Assert.Equal("floor.vinyl", view.Selection.Preset.Payload.GetProperty("materials").GetProperty("corridor").GetProperty("floor").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutStyle(client, revision, body, "\"0\"")).StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='RevisionStyle' AND target_id='{revision}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE revision_style_selections SET data='{{}}' WHERE revision_id='{revision}'"));

        // Another tenant cannot see the revision; the next revision of the Building gets the choice as a suggestion only.
        var stranger = await CreateOrganizationAsync("style-x-" + suffix.ToLowerInvariant());
        await CreateAccountAsync($"style-{suffix.ToLowerInvariant()}@example.test", UserRole.OrganizationUser, stranger.Id);
        using var other = factory!.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"style-{suffix.ToLowerInvariant()}@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/revisions/{revision}/style")).StatusCode);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        var next = await SeedVerifiedIfcRevisionIn(building, "v2");
        var fresh = (await client.GetFromJsonAsync<RevisionStyleView>($"/api/revisions/{next}/style", Json))!;
        Assert.Null(fresh.Selection);
        Assert.Equal((revision, preset.Version), (fresh.PreviousSelection!.FromRevisionId, fresh.PreviousSelection.PresetVersionId));
    }
}

public sealed class RevisionStyleUnitTests
{
    public static JsonObject Samples() => JsonNode.Parse(File.ReadAllText(Path.Combine(EditorContractFixtures.ContractDirectory, "..", "..", "style", "v1", "fixtures", "style-presets.sample.json")))!.AsObject();

    private static List<string> Issues(string kind, JsonObject payload) =>
        LibraryContent.Validate(kind, new LibraryVersionInput("x", payload)).Select(x => $"{x.Code} {x.Path}").ToList();

    [Fact]
    public void Sample_presets_and_schemes_are_valid_library_payloads()
    {
        var samples = Samples();
        foreach (var (_, preset) in samples["StylePreset"]!.AsObject()) Assert.Empty(Issues("StylePreset", preset!.DeepClone().AsObject()));
        foreach (var (_, scheme) in samples["ColorScheme"]!.AsObject()) Assert.Empty(Issues("ColorScheme", scheme!.DeepClone().AsObject()));
    }

    [Fact]
    public void Preset_scheme_and_selection_shapes_are_checked()
    {
        var preset = Samples()["StylePreset"]!["SCHOOL"]!.DeepClone().AsObject();
        preset["materials"]!["default"]!.AsObject().Remove("ceiling");
        preset["materials"]!["wc"]!["roof"] = "floor.tile";
        preset["materials"]!["corridor"]!["floor"] = "Floor Tile";
        Assert.Equal(["FIELD_REQUIRED $.payload.materials.default", "FIELD_UNKNOWN $.payload.materials.wc.roof", "FIELD_INVALID $.payload.materials.corridor.floor"],
            Issues("StylePreset", preset));
        var scheme = Samples()["ColorScheme"]!["WARM-NEUTRAL"]!.DeepClone().AsObject();
        scheme["colors"]!["wall"] = "red"; scheme["colors"]!.AsObject().Remove("skirting");
        Assert.Equal(["FIELD_INVALID $.payload.colors.wall", "FIELD_INVALID $.payload.colors.skirting"], Issues("ColorScheme", scheme));

        var request = new RevisionStyleRequest(Guid.NewGuid(), Guid.Empty,
            [new("wc", "floor", "floor.tile"), new("wc", "floor", "floor.vinyl"), new("Lobby", "roof", "stone")]);
        Assert.Equal(["FIELD_INVALID $.schemeVersionId", "ID_DUPLICATE $.overrides[1]", "FIELD_INVALID $.overrides[2].room", "FIELD_INVALID $.overrides[2].surface"],
            StyleRules.Validate(request).Select(x => $"{x.Code} {x.Path}"));
    }
}
