using Fire3D.Application.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

// Store-level branches of the pre-account email proof, run through the application's own DI scope.
public sealed partial class AuthIntegrationTests
{
    private async Task<T> InOtpScope<T>(Func<IRegistrationOtpService, IRegistrationOtpDeliveryQueue, Task<T>> action)
    {
        using var scope = factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IRegistrationOtpService>(),
            scope.ServiceProvider.GetRequiredService<IRegistrationOtpDeliveryQueue>());
    }

    private async Task<RegistrationOtpEmailJob> RequestAndClaimOtpAsync(string email, string? ip = null)
    {
        Assert.True((await InOtpScope((otp, _) => otp.RequestOtpAsync(email, ip, default))).IsSuccess);
        var job = Assert.IsType<RegistrationOtpEmailJob>(await InOtpScope((_, queue) => queue.ClaimAsync(default)));
        Assert.Equal(email, job.Email);
        return job;
    }

    [PostgresFact]
    public async Task Registration_otp_repeat_request_within_the_cooldown_is_accepted_without_a_new_code()
    {
        const string email = "cooldown@example.test";
        Assert.True((await InOtpScope((otp, _) => otp.RequestOtpAsync(email, "192.0.2.10", default))).IsSuccess);
        var repeat = await InOtpScope((otp, _) => otp.RequestOtpAsync(email, null, default));
        Assert.True(repeat.IsSuccess);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM registration_otp_email_jobs WHERE email='{email}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM registration_email_challenges WHERE email='{email}' AND superseded_at IS NULL"));
    }

    [PostgresFact]
    public async Task Registration_otp_wrong_codes_are_counted_and_the_fifth_supersedes_the_challenge()
    {
        const string email = "wrong-code@example.test";
        var missing = await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, "123456", default));
        Assert.Equal(("INVALID_OTP", 400), (missing.Error?.Code, missing.Error?.Status));

        var job = await RequestAndClaimOtpAsync(email);
        var wrong = job.Otp == "000000" ? "111111" : "000000";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, wrong, default));
            Assert.Equal("INVALID_OTP", result.Error?.Code);
            Assert.Equal((long)attempt, Convert.ToInt64(await ScalarAsync($"SELECT failed_attempts FROM registration_email_challenges WHERE email='{email}'")));
        }
        Assert.Equal(true, await ScalarAsync($"SELECT superseded_at IS NOT NULL FROM registration_email_challenges WHERE email='{email}'"));
        // The correct code no longer works once the challenge is superseded.
        Assert.Equal("INVALID_OTP", (await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, job.Otp, default))).Error?.Code);
    }

    [PostgresFact]
    public async Task Registration_otp_repeat_verify_returns_the_same_proof_until_it_is_consumed()
    {
        const string email = "proof-replay@example.test";
        var job = await RequestAndClaimOtpAsync(email);
        var first = await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, job.Otp, default));
        Assert.True(first.IsSuccess);
        var again = await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, job.Otp, default));
        Assert.Equal(first.Value!.RegistrationToken, again.Value!.RegistrationToken);
        Assert.Equal(first.Value.ExpiresAt, again.Value.ExpiresAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM registration_email_challenges WHERE email='{email}' AND verified_at IS NOT NULL"));

        var blank = await InOtpScope((otp, _) => otp.ConsumeRegistrationTokenAsync(email, " ", default));
        Assert.Equal(("EMAIL_VERIFICATION_REQUIRED", 400), (blank.Error?.Code, blank.Error?.Status));
        Assert.True((await InOtpScope((otp, _) => otp.ConsumeRegistrationTokenAsync(email, first.Value.RegistrationToken, default))).IsSuccess);
        Assert.Equal("EMAIL_VERIFICATION_REQUIRED",
            (await InOtpScope((otp, _) => otp.ConsumeRegistrationTokenAsync(email, first.Value.RegistrationToken, default))).Error?.Code);

        var used = await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, job.Otp, default));
        Assert.Equal(("REGISTRATION_PROOF_EXPIRED", 400), (used.Error?.Code, used.Error?.Status));
    }

    [PostgresFact]
    public async Task Registration_otp_expired_challenge_is_rejected_without_counting_an_attempt()
    {
        const string email = "expired-code@example.test";
        var job = await RequestAndClaimOtpAsync(email);
        await ExecuteAsync($"UPDATE registration_email_challenges SET expires_at=now()-interval '1 second' WHERE email='{email}'");
        Assert.Equal("INVALID_OTP", (await InOtpScope((otp, _) => otp.VerifyOtpAsync(email, job.Otp, default))).Error?.Code);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync($"SELECT failed_attempts FROM registration_email_challenges WHERE email='{email}'")));
    }

    [PostgresFact]
    public async Task Registration_otp_delivery_completion_and_failure_are_fenced_by_the_lease()
    {
        var sent = await RequestAndClaimOtpAsync("delivered@example.test");
        await InOtpScope(async (_, queue) => { await queue.CompleteAsync(sent, default); return true; });
        Assert.Equal("Succeeded", await ScalarAsync($"SELECT status FROM registration_otp_email_jobs WHERE id='{sent.Id}'"));
        await InOtpScope(async (_, queue) => { await queue.FailAsync(sent, true, default); return true; }); // Stale receipt: no effect.
        Assert.Equal("Succeeded", await ScalarAsync($"SELECT status FROM registration_otp_email_jobs WHERE id='{sent.Id}'"));

        var transient = await RequestAndClaimOtpAsync("retry@example.test");
        Assert.Equal(1, transient.Attempt);
        await InOtpScope(async (_, queue) => { await queue.FailAsync(transient, false, default); return true; });
        Assert.Equal(true, await ScalarAsync($"SELECT status='Pending' AND lease_token IS NULL AND available_at>now() FROM registration_otp_email_jobs WHERE id='{transient.Id}'"));
        Assert.Null(await InOtpScope((_, queue) => queue.ClaimAsync(default)));

        await ExecuteAsync($"UPDATE registration_otp_email_jobs SET available_at=now()-interval '1 second' WHERE id='{transient.Id}'");
        var retried = Assert.IsType<RegistrationOtpEmailJob>(await InOtpScope((_, queue) => queue.ClaimAsync(default)));
        Assert.Equal((transient.Id, 2, transient.Otp), (retried.Id, retried.Attempt, retried.Otp));
        Assert.NotEqual(transient.LeaseToken, retried.LeaseToken);
        await InOtpScope(async (_, queue) => { await queue.FailAsync(retried, true, default); return true; });
        Assert.Equal("Dead", await ScalarAsync($"SELECT status FROM registration_otp_email_jobs WHERE id='{transient.Id}'"));
    }
}
