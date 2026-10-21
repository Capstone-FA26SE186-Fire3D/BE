using System.Net;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.CreateAccount;
using Fire3D.Application.Billing;
using Fire3D.Application.Reporting;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Reporting;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Audit field filtering, billing timestamps, small auth handlers and host configuration checks that need no database.</summary>
public sealed class SupportingContractTests
{
    private static AuditLog Audit(string entity, AuditAction action, object? before, object? after) => new()
    {
        Id = Guid.NewGuid(), TargetEntity = entity, Action = action, ActorType = "User", CreatedAt = DateTime.UtcNow,
        OldValues = before is null ? null : before as string ?? JsonSerializer.Serialize(before), NewValues = after is null ? null : after as string ?? JsonSerializer.Serialize(after)
    };

    [Fact]
    public void Audit_changes_expose_only_allow_listed_typed_fields_per_entity()
    {
        var building = Guid.NewGuid();
        var entitlement = SafeAuditChanges.Read(Audit("service_entitlements", AuditAction.Payment, new { },
            new { learnerLimit = 20, startsAt = "2026-10-01T07:00:00+07:00", buildingId = building, commercialVersion = 7, note = "free text" }));
        Assert.Equal(new AuditFieldChange[] { new("commercialVersion", null, 7m), new("learnerLimit", null, 20m), new("startsAt", null, DateTimeOffset.Parse("2026-10-01T00:00:00Z")), new("buildingId", null, building) }, entitlement);
        var policy = Guid.NewGuid();
        var grant = SafeAuditChanges.Read(Audit("billing_ai_quota_grants", AuditAction.Grant, null, new { quotaUnits = 500, quotaUnit = "tokens", rollover = "None", policyVersionId = policy }));
        Assert.Equal(new AuditFieldChange[] { new("quotaUnits", null, 500m), new("quotaUnit", null, "tokens"), new("policyVersionId", null, policy), new("rollover", null, "None") }, grant);
        // Values outside the allow-list read as absent, so they never reach the report.
        Assert.Empty(SafeAuditChanges.Read(Audit("billing_ai_quota_grants", AuditAction.Grant, null, new { quotaUnit = "dollars", rollover = "Forever", quotaUnits = -1 })));
        Assert.Empty(SafeAuditChanges.Read(Audit("billing_ai_quota_grants", AuditAction.Update, null, new { quotaUnits = 500 })));
        Assert.Equal([new AuditFieldChange("isActive", true, false)], SafeAuditChanges.Read(Audit("service_packages", AuditAction.Update, new { isActive = true, unitPrice = 100 }, new { isActive = false, unitPrice = 100 })));
        var reviewer = Guid.NewGuid();
        Assert.Equal(new AuditFieldChange[] { new("status", "Submitted", "Approved"), new("reviewedBy", null, reviewer) },
            SafeAuditChanges.Read(Audit("scenario_content_reviews", AuditAction.Update, new { status = "Submitted" }, new { status = "Approved", reviewedBy = reviewer })));
        Assert.Empty(SafeAuditChanges.Read(Audit("quotations", AuditAction.Update, "{broken", "{}")));
    }

    [Fact]
    public void Audit_reason_and_rejected_review_ignore_unrecognised_or_malformed_writers()
    {
        Assert.Null(SafeAuditChanges.Reason(Audit("SupportTicket", AuditAction.Support, null, "{not json")));
        Assert.Null(SafeAuditChanges.Reason(Audit("quotations", AuditAction.Update, null, new { reason = "No status was written" })));
        Assert.Equal("Duplicate order", SafeAuditChanges.Reason(Audit("quotations", AuditAction.Update, null, new { Status = "Cancelled", reason = "Duplicate order" })));
        Assert.Null(SafeAuditChanges.RejectedReview(Audit("ScenarioVersion", AuditAction.Reject, null, "[1,")));
        var review = Guid.NewGuid();
        Assert.Equal(review, SafeAuditChanges.RejectedReview(Audit("ScenarioVersion", AuditAction.Reject, null, new { auditContractVersion = 1, status = "Rejected", reviewId = review })));
    }

    [Fact]
    public void Billing_timestamps_round_trip_with_their_offset()
    {
        var request = new DiscountWriteRequest("LOYAL", "Percent", 10, 2, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var json = JsonSerializer.Serialize(request);
        Assert.Contains("\"ValidFrom\":\"2026-10-02T12:00:00+07:00\"", json);
        Assert.Contains("\"ValidUntil\":\"2026-11-01T00:00:00+00:00\"", json);
        Assert.Equal(request, JsonSerializer.Deserialize<DiscountWriteRequest>(json));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Resend_verification_rejects_an_invalid_email_without_queueing(string email)
    {
        var queue = ResetProxy.For<IEmailVerificationQueue>((m, _) => throw new InvalidOperationException("Unexpected: " + m));
        var result = await new ResendVerificationCommandHandler(queue).Handle(new(email), default);
        Assert.Equal(("INVALID_EMAIL", 400), (result.Error?.Code, result.Error?.Status));
    }

    [Fact]
    public async Task Verification_handlers_queue_the_normalized_email_and_report_an_unusable_token()
    {
        var queued = new List<string>(); var valid = false;
        var queue = ResetProxy.For<IEmailVerificationQueue>((method, args) => method switch
        {
            "EnqueueAsync" when args.Length == 2 => Add(queued, (string)args[0]!),
            "VerifyAsync" => Task.FromResult(valid),
            _ => throw new InvalidOperationException("Unexpected: " + method)
        });
        Assert.True((await new ResendVerificationCommandHandler(queue).Handle(new("  User@Example.TEST "), default)).IsSuccess);
        Assert.Equal(["user@example.test"], queued);
        var rejected = await new VerifyEmailCommandHandler(queue).Handle(new("token"), default);
        Assert.Equal(("INVALID_VERIFICATION_TOKEN", 400), (rejected.Error?.Code, rejected.Error?.Status));
        valid = true;
        Assert.True((await new VerifyEmailCommandHandler(queue).Handle(new("token"), default)).Value);
    }
    private static Task Add(List<string> list, string value) { list.Add(value); return Task.CompletedTask; }

    [Theory]
    [InlineData("bad-email", "123456", "INVALID_EMAIL", "email")]
    [InlineData("user@example.test", "", "INVALID_OTP", "otp")]
    [InlineData("user@example.test", "12345", "INVALID_OTP", "otp")]
    [InlineData("user@example.test", "12a456", "INVALID_OTP", "otp")]
    public async Task Registration_otp_verification_validates_before_the_service(string email, string otp, string code, string field)
    {
        var service = ResetProxy.For<IRegistrationOtpService>((m, _) => throw new InvalidOperationException("Unexpected: " + m));
        var result = await new VerifyRegistrationOtpCommandHandler(service).Handle(new(email, otp), default);
        Assert.Equal((code, 400), (result.Error?.Code, result.Error?.Status));
        Assert.True(result.Error!.Errors!.ContainsKey(field));
    }

    [Fact]
    public async Task Registration_otp_verification_passes_the_normalized_email_to_the_service()
    {
        string? seen = null;
        var service = ResetProxy.For<IRegistrationOtpService>((method, args) =>
        {
            Assert.Equal("VerifyOtpAsync", method); seen = (string)args[0]!;
            return Task.FromResult(AuthResult<RegistrationOtpVerificationResponse>.Fail("OTP_INVALID", "fixture", 400));
        });
        Assert.Equal("OTP_INVALID", (await new VerifyRegistrationOtpCommandHandler(service).Handle(new(" New@Example.TEST", "012345"), default)).Error?.Code);
        Assert.Equal("new@example.test", seen);
    }

    private sealed class NoTransaction : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static ISender Sender(IAuthStore store)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(CreateAccountCommand).Assembly));
        services.AddSingleton(store); services.AddSingleton(TimeProvider.System);
        services.AddSingleton(ResetProxy.For<IPasswordService>((_, _) => "fixture-hash"));
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    [Fact]
    public async Task Account_creation_rechecks_the_admin_inside_the_transaction_and_reports_a_duplicate_email()
    {
        var admin = new User { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, IsActive = true, Email = "admin@example.test" };
        var lookups = 0; var created = 0;
        IAuthStore Store(Func<int, User> actor, bool insert) => ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserAsync" => Task.FromResult<User?>(actor(++lookups)),
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new NoTransaction()),
            "OrganizationIsActiveAsync" => Task.FromResult(true),
            "TryCreateUserAsync" => Task.FromResult(insert && ++created > 0),
            _ => throw new InvalidOperationException("Unexpected: " + method)
        });
        var request = new CreateAccountRequest("manager@example.test", "StrongPassword12!", "Manager", UserRole.OrganizationUser, Guid.NewGuid(), null);
        // The admin is demoted between the handler check and the locked re-read.
        var demoted = await Sender(Store(n => n == 1 ? admin : new User { Id = admin.Id, Role = UserRole.OrganizationUser, OrganizationId = Guid.NewGuid(), IsActive = true }, true))
            .Send(new CreateAccountCommand(admin.Id, request), default);
        Assert.Equal(("FORBIDDEN", 403), (demoted.Error?.Code, demoted.Error?.Status));
        Assert.Equal((2, 0), (lookups, created));
        var duplicate = await Sender(Store(_ => admin, false)).Send(new CreateAccountCommand(admin.Id, request), default);
        Assert.Equal(("EMAIL_EXISTS", 409), (duplicate.Error?.Code, duplicate.Error?.Status));
    }

    private static WebApplicationFactory<Program> Host(params string[] proxies) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=fire3d_unused_host_test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:Issuer"] = "host-tests", ["Jwt:Audience"] = "host-tests", ["Mailgun:ApiKey"] = "test-key", ["Mailgun:Domain"] = "example.test",
            ["Mailgun:From"] = "noreply@example.test", ["AuthEmail:FrontendUrl"] = "https://example.test", ["AuthEmail:VerificationUrl"] = "https://example.test/verify",
            ["AuthEmail:WorkerEnabled"] = "false", ["PayOS:WorkerEnabled"] = "false", ["Redis:Enabled"] = "false", ["PlatformCache:WorkerEnabled"] = "false"
        };
        for (var i = 0; i < proxies.Length; i++) settings[$"ForwardedHeaders:KnownProxies:{i}"] = proxies[i];
        web.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        web.ConfigureServices(services => services.RemoveAll<IHostedService>());
    });

    [Fact]
    public async Task Forwarded_headers_trust_only_configured_proxy_addresses()
    {
        await using (var host = Host("10.1.2.3", "fd00::7"))
        {
            var options = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            Assert.Contains(IPAddress.Parse("10.1.2.3"), options.KnownProxies); Assert.Contains(IPAddress.Parse("fd00::7"), options.KnownProxies);
            Assert.Equal(1, options.ForwardLimit);
            using var client = host.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/version")).StatusCode);
        }
        await using var invalid = Host("proxy.internal");
        var error = Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
        Assert.Contains("ForwardedHeaders:KnownProxies must contain only IP addresses.", error.ToString());
    }
}
