using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.FireSafety;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Reporting;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

// Unit tests of pure rules (no database, no HTTP): each one pins a rule the gates and controllers rely on.
public sealed class ReportingRangeUnitTests
{
    private static readonly DateTime AsOf = new(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Without_from_and_to_the_range_is_the_last_default_days()
    {
        Assert.True(ReportingRange.TryParse(null, null, AsOf, out var range));
        Assert.Equal((AsOf.AddDays(-30), AsOf), range);
        Assert.True(ReportingRange.TryParse(null, null, AsOf, out var year, maxDays: 366, defaultDays: 365));
        Assert.Equal(AsOf.AddDays(-365), year.From);
    }

    [Fact]
    public void Timestamps_need_a_time_and_a_zone_and_are_read_as_utc()
    {
        Assert.True(ReportingRange.TryParse("2026-01-01T07:00:00+07:00", "2026-01-31T00:00:00Z", AsOf, out var range));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), range.From);
        Assert.Equal(DateTimeKind.Utc, range.To.Kind);
        Assert.False(ReportingRange.TryParse("2026-01-01", "2026-01-31T00:00:00Z", AsOf, out _));
        Assert.False(ReportingRange.TryParse("2026-01-01T00:00:00", "2026-01-31T00:00:00Z", AsOf, out _));
        Assert.False(ReportingRange.TryParse("yesterday", null, AsOf, out _));
    }

    [Fact]
    public void The_range_must_move_forward_and_stay_within_max_days()
    {
        Assert.False(ReportingRange.TryParse("2026-02-01T00:00:00Z", "2026-01-01T00:00:00Z", AsOf, out _));
        Assert.False(ReportingRange.TryParse("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", AsOf, out _));
        Assert.True(ReportingRange.TryParse("2026-01-01T00:00:00Z", "2026-04-01T00:00:00Z", AsOf, out _));      // 90 days
        Assert.False(ReportingRange.TryParse("2026-01-01T00:00:00Z", "2026-04-02T00:00:00Z", AsOf, out _));     // 91 days
        Assert.True(ReportingRange.TryParse("2026-01-01T00:00:00Z", "2026-04-02T00:00:00Z", AsOf, out _, maxDays: 366));
    }
}

public sealed class OrganizationAiInputUnitTests
{
    [Fact]
    public void Text_is_required_plain_and_bounded()
    {
        Assert.Equal("FIELD_REQUIRED", Assert.Single(OrganizationAiInput.Text("  ", "$.prompt")).Code);
        Assert.Equal("FIELD_REQUIRED", Assert.Single(OrganizationAiInput.Text(null, "$.prompt")).Code);
        Assert.Equal("VALUE_TOO_LONG", Assert.Single(OrganizationAiInput.Text(new string('a', 8001), "$.prompt")).Code);
        Assert.Equal("VALUE_TOO_LONG", Assert.Single(OrganizationAiInput.Text("12345678901", "$.title", 10)).Code);
        Assert.Equal("MARKUP_NOT_ALLOWED", Assert.Single(OrganizationAiInput.Text("Cháy <b>bếp</b>", "$.prompt")).Code);
        Assert.Equal("MARKUP_NOT_ALLOWED", Assert.Single(OrganizationAiInput.Text("bell\u0007", "$.prompt")).Code);
        Assert.Empty(OrganizationAiInput.Text("Tầng 1\nbếp\tcháy\r\n", "$.prompt"));
        Assert.Empty(OrganizationAiInput.Text("2 < 3 people", "$.prompt"));
        Assert.Equal("$.prompt", OrganizationAiInput.Text("", "$.prompt")[0].Path);
    }

    [Fact]
    public void Sources_need_a_known_kind_an_id_and_no_duplicates()
    {
        Assert.Empty(OrganizationAiInput.Sources(null));
        var id = Guid.NewGuid();
        Assert.Empty(OrganizationAiInput.Sources([new("LearnPost", id), new("KnowledgeSource", id), new("LibraryVersion", Guid.NewGuid())]));
        var bad = OrganizationAiInput.Sources([new("Video", Guid.NewGuid()), new("LearnPost", Guid.Empty)]);
        Assert.Equal(new[] { "$.sources[0]", "$.sources[1]" }, bad.Select(x => x.Path).ToArray());
        Assert.All(bad, x => Assert.Equal("VALUE_UNSUPPORTED", x.Code));
        Assert.Equal("DUPLICATE_SOURCE", Assert.Single(OrganizationAiInput.Sources([new("LearnPost", id), new("LearnPost", id)])).Code);
        var many = Enumerable.Range(0, 101).Select(_ => new AiSourceRef("LearnPost", Guid.NewGuid())).ToList();
        Assert.Equal("VALUE_TOO_LONG", Assert.Single(OrganizationAiInput.Sources(many)).Code);
        // A Trainee may not pin Library versions.
        Assert.Equal("VALUE_UNSUPPORTED", Assert.Single(OrganizationAiInput.Sources([new("LibraryVersion", Guid.NewGuid())], OrganizationAiInput.TraineeSourceKinds)).Code);
    }

    [Fact]
    public void Source_registration_checks_visibility_owner_label_and_dates()
    {
        var org = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(OrganizationAiInput.SourceRegistration("Common", "QCVN 06:2022", "2022.1", null, "VN", from, from.AddYears(1)));
        Assert.Empty(OrganizationAiInput.SourceRegistration("Organization", "Nội quy PCCC", "v1", org, null, null, null));
        Assert.Contains(OrganizationAiInput.SourceRegistration("Public", "T", "v1", null, null, null, null), x => x.Path == "$.visibility");
        Assert.Contains(OrganizationAiInput.SourceRegistration("Organization", "T", "v1", null, null, null, null), x => x.Path == "$.organizationId");
        Assert.Contains(OrganizationAiInput.SourceRegistration("Common", "T", "v1", org, null, null, null), x => x.Path == "$.organizationId");
        Assert.Contains(OrganizationAiInput.SourceRegistration("Common", "T", "v 1", null, null, null, null), x => x.Path == "$.versionLabel");
        Assert.Contains(OrganizationAiInput.SourceRegistration("Common", "T", "v1", null, null, from, from), x => x.Code == "VALUE_OUT_OF_RANGE");
        Assert.Contains(OrganizationAiInput.SourceRegistration("Common", "", "v1", null, null, null, null), x => x.Path == "$.title");
    }

    [Fact]
    public void Only_tokens_and_requests_are_quota_units()
    {
        Assert.True(OrganizationAiInput.SupportedQuotaUnit("tokens"));
        Assert.True(OrganizationAiInput.SupportedQuotaUnit("requests"));
        Assert.False(OrganizationAiInput.SupportedQuotaUnit("Tokens"));
        Assert.False(OrganizationAiInput.SupportedQuotaUnit(null));
        Assert.Matches(OrganizationAiInput.Sha256(), new string('a', 64));
        Assert.DoesNotMatch(OrganizationAiInput.Sha256(), new string('A', 64));
    }
}

public sealed class OrganizationAiOptionsUnitTests
{
    [Fact]
    public void Scenario_drafts_wait_longer_than_answers_unless_configured()
    {
        var options = new OrganizationAiOptions();
        Assert.Equal(60, options.TimeoutFor("Answer"));
        Assert.Equal(60, options.TimeoutFor(null));
        Assert.Equal(150, options.TimeoutFor("ScenarioDraft"));                 // lease 180 minus 30 s
        Assert.Equal(100, new OrganizationAiOptions { ScenarioDraftTimeoutSeconds = 100 }.TimeoutFor("ScenarioDraft"));
        Assert.Equal(600, new OrganizationAiOptions { TimeoutSeconds = 60, LeaseSeconds = 3600 }.TimeoutFor("ScenarioDraft"));
        Assert.Equal(90, new OrganizationAiOptions { TimeoutSeconds = 90, LeaseSeconds = 100 }.TimeoutFor("ScenarioDraft"));
    }

    [Fact]
    public void Configured_needs_enabled_an_https_or_loopback_url_and_sane_timing()
    {
        Assert.True(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.example.test" }.Configured);
        Assert.True(new OrganizationAiOptions { Enabled = true, BaseUrl = "http://localhost:8000" }.Configured);
        Assert.False(new OrganizationAiOptions { Enabled = false, BaseUrl = "https://ai.example.test" }.Configured);
        Assert.Null(new OrganizationAiOptions { Enabled = false }.Problem);

        var plainHttp = new OrganizationAiOptions { Enabled = true, BaseUrl = "http://ai.example.test" };
        Assert.False(plainHttp.Configured);
        Assert.Contains("BaseUrl", plainHttp.Problem);

        var timing = new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.example.test", TimeoutSeconds = 60, LeaseSeconds = 60 };
        Assert.False(timing.TimingValid);
        Assert.Contains("timing", timing.Problem);
        Assert.False(new OrganizationAiOptions { TimeoutSeconds = 4 }.TimingValid);
        Assert.False(new OrganizationAiOptions { MaxAttempts = 11 }.TimingValid);
        Assert.False(new OrganizationAiOptions { ScenarioDraftTimeoutSeconds = 180 }.TimingValid);   // not below the lease
        Assert.True(new OrganizationAiOptions { ScenarioDraftTimeoutSeconds = 170 }.TimingValid);
    }

    [Fact]
    public void Unreadable_settings_keep_ai_off_and_say_why()
    {
        var options = new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.example.test" };
        options.MarkUnreadable();
        Assert.False(options.Configured);
        Assert.Contains("could not be read", options.Problem);
    }
}

public sealed class IfcProcessingOptionsUnitTests
{
    [Fact]
    public void The_configured_daily_limit_is_1_to_100000_and_defaults_to_20()
    {
        Assert.Equal(20, new IfcProcessingOptions().DailyLimitPerOrganization);
        Assert.False(IfcProcessingOptions.IsValidDailyLimit(0));
        Assert.True(IfcProcessingOptions.IsValidDailyLimit(1));
        Assert.True(IfcProcessingOptions.IsValidDailyLimit(100_000));
        Assert.False(IfcProcessingOptions.IsValidDailyLimit(100_001));
        Assert.False(IfcProcessingOptions.IsValidDailyLimit(-1));
    }
}

public sealed class MemoryLoginAttemptLimiterUnitTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_email_is_blocked_after_max_failures_until_its_window_ends()
    {
        var limiter = new MemoryLoginAttemptLimiter(new LoginAttemptOptions { MaxFailures = 3, WindowMinutes = 15 });
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0));
        limiter.RecordFailure("a@example.test", T0);
        limiter.RecordFailure("a@example.test", T0.AddMinutes(1));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(1)));
        limiter.RecordFailure("a@example.test", T0.AddMinutes(2));
        Assert.Equal(13 * 60, limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(2)));
        Assert.Equal(1, limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(15).AddMilliseconds(-1)));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(15)));
        // Another account is never affected.
        Assert.Null(limiter.RetryAfterSeconds("b@example.test", T0.AddMinutes(2)));
    }

    [Fact]
    public void A_failure_after_the_window_starts_a_new_one_and_reset_clears_it()
    {
        var limiter = new MemoryLoginAttemptLimiter(new LoginAttemptOptions { MaxFailures = 2, WindowMinutes = 10 });
        limiter.RecordFailure("a@example.test", T0);
        limiter.RecordFailure("a@example.test", T0.AddMinutes(11));             // new window, count 1
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(11)));
        limiter.RecordFailure("a@example.test", T0.AddMinutes(12));
        Assert.NotNull(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(12)));
        limiter.Reset("a@example.test");
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(12)));
    }

    [Fact]
    public void Zero_settings_fall_back_to_one_failure_and_one_minute()
    {
        var limiter = new MemoryLoginAttemptLimiter(new LoginAttemptOptions { MaxFailures = 0, WindowMinutes = 0 });
        limiter.RecordFailure("a@example.test", T0);
        Assert.Equal(60, limiter.RetryAfterSeconds("a@example.test", T0));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", T0.AddMinutes(1)));
    }
}

public sealed class ScenarioPlanFieldsUnitTests
{
    [Fact]
    public void A_situation_names_audience_working_hours_and_every_assumption_field()
    {
        Assert.Equal(new[] { "$.audience", "$.workingHours", "$.assumption" },
            ScenarioStateV1Validator.PlanIssues(new JsonObject()).Select(x => x.Path).ToArray());
        var partial = new JsonObject { ["audience"] = "Evacuee", ["workingHours"] = "OffHours", ["assumption"] = new JsonObject { ["time"] = "22:00" } };
        Assert.Equal(new[] { "$.assumption.location", "$.assumption.cause", "$.assumption.peoplePresent" },
            ScenarioStateV1Validator.PlanIssues(partial).Select(x => x.Path).ToArray());
        var complete = new JsonObject
        {
            ["audience"] = "EvacuationGuide", ["workingHours"] = "InHours",
            ["assumption"] = new JsonObject { ["time"] = "10:00", ["location"] = "Bếp tầng 3", ["cause"] = "Chập điện", ["peoplePresent"] = 120 }
        };
        Assert.Empty(ScenarioStateV1Validator.PlanIssues(complete));
        Assert.Empty(ScenarioStateV1Validator.PlanIssues(null));
        Assert.Empty(ScenarioStateV1Validator.PlanIssues(JsonValue.Create(1)));
        Assert.All(ScenarioStateV1Validator.PlanIssues(new JsonObject()), x => Assert.Equal("FIELD_REQUIRED", x.Code));
    }

    [Fact]
    public void Plan_fields_are_optional_on_a_legacy_draft_but_checked_when_present()
    {
        var state = EditorContractFixtures.State();
        Assert.DoesNotContain(ScenarioStateV1Validator.ValidateShape(state), x => x.Path.StartsWith("$.audience") || x.Path.StartsWith("$.workingHours"));
        state["audience"] = "Everyone";
        state["workingHours"] = "Weekend";
        var issues = ScenarioStateV1Validator.ValidateShape(state);
        Assert.Contains(issues, x => x.Path == "$.audience");
        Assert.Contains(issues, x => x.Path == "$.workingHours");
    }
}

public sealed class FireInfrastructureRulesUnitTests
{
    private static JsonObject Item(string fixtureId, string category)
    {
        var o = EditorContractFixtures.State()["objects"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == fixtureId)!.DeepClone().AsObject();
        o["category"] = category;
        return o;
    }

    [Fact]
    public void Items_are_an_array_with_a_known_category_and_the_matching_kind()
    {
        Assert.Equal("$.items", Assert.Single(FireInfrastructureRules.Validate(null, null, new JsonObject(), false, null)).Path);
        var issues = FireInfrastructureRules.Validate(null, null, new JsonArray(Item("goal-exit", "Kitchen"), Item("ext-1", "Exit"), Item("goal-exit", "Equipment")), false, null);
        Assert.Contains(issues, x => x.Path == "$.items[0].category" && x.Code == "FIELD_INVALID");
        Assert.Contains(issues, x => x.Path == "$.items[1].kind" && x.Code == "INFRASTRUCTURE_KIND_MISMATCH");
        Assert.Contains(issues, x => x.Path == "$.items[2].kind" && x.Code == "INFRASTRUCTURE_KIND_MISMATCH");
    }

    [Theory]
    [InlineData("item.towel")]
    [InlineData("bait.belongings")]
    [InlineData("door.exit_locked")]
    [InlineData("door.fire_wedged")]
    [InlineData("transport.elevator")]
    public void Scenario_items_door_states_and_elevators_are_not_fixed_equipment(string capability)
    {
        var item = Item("ext-1", "Equipment");
        item["capability"]!["id"] = capability;
        Assert.Contains(FireInfrastructureRules.Validate(null, null, new JsonArray(item), false, null),
            x => x.Path == "$.items[0].capability.id" && x.Code == "INFRASTRUCTURE_CAPABILITY_NOT_FIXED");
    }

    [Fact]
    public void Submitting_needs_an_exit_while_saving_does_not()
    {
        var withoutExit = new JsonArray(Item("ext-1", "Equipment"));
        Assert.DoesNotContain(FireInfrastructureRules.Validate(null, null, withoutExit, false, null), x => x.Code == "EXIT_REQUIRED");
        Assert.Contains(FireInfrastructureRules.Validate(null, null, withoutExit.DeepClone(), true, null), x => x.Code == "EXIT_REQUIRED");
        Assert.DoesNotContain(FireInfrastructureRules.Validate(null, null, new JsonArray(Item("goal-exit", "Exit")), true, null), x => x.Code == "EXIT_REQUIRED");
    }

    [Fact]
    public void Items_become_an_editor_document_without_category_and_paths_map_back()
    {
        var document = FireInfrastructureRules.Document(new JsonObject { ["artifactId"] = "a" }, "1.4.0", new JsonArray(Item("goal-exit", "Exit")));
        Assert.Equal(EditorContract.V1, document["schemaVersion"]!.GetValue<string>());
        Assert.Null(document["objects"]![0]!["category"]);
        Assert.Equal("1.4.0", document["runtimeVersion"]!.GetValue<string>());
        Assert.Equal("a", document["geometry"]!["artifactId"]!.GetValue<string>());
        Assert.Null(FireInfrastructureRules.Document(null, null, new JsonArray())["geometry"]);
        Assert.Equal("$.items[2].placement", FireInfrastructureRules.ItemPath("$.objects[2].placement"));
        Assert.Equal("$.geometry", FireInfrastructureRules.ItemPath("$.geometry"));
        Assert.Equal(FireInfrastructureRules.ScenarioItemCapabilities, OrganizationAiInput.ScenarioOnlyCapabilities);
    }
}
