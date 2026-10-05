using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Commands.FirebaseLogin;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleExchangeHardeningTests
{
    [Fact]
    public async Task Linked_identity_issues_session_with_database_role_and_tenant()
    {
        var user = new User { Id = Guid.NewGuid(), FirebaseUid = "uid", Email = "db@example.test", Role = UserRole.OrganizationUser,
            OrganizationId = Guid.NewGuid(), IsActive = true };
        var saved = new List<string>();
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(user),
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction()),
            "OrganizationIsActiveAsync" => Task.FromResult(true),
            "UpdateLoginAsync" or "AddRefreshTokenAsync" or "WriteAuditAsync" => Save(method),
            _ => throw new Exception(method)
        });
        Task Save(string name) { saved.Add(name); return Task.CompletedTask; }
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity("uid", "provider@example.test")));
        var tokens = ResetProxy.For<ITokenService>((method, args) => method switch
        {
            "get_RefreshTokenLifetime" => TimeSpan.FromDays(7),
            "CreateRefreshToken" => "refresh",
            "HashRefreshToken" => "hash",
            "CreateAccessToken" => Access(args),
            _ => throw new Exception(method)
        });
        AccessTokenValue Access(object?[] args) { Assert.Same(user, args[0]); return new("access", DateTime.UtcNow.AddMinutes(15)); }
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs()).Handle(new("token"), default);
        Assert.Equal("Authenticated", result.Value?.Status);
        Assert.Equal(user.OrganizationId, result.Value?.Authentication?.User.OrganizationId);
        Assert.Equal(UserRole.OrganizationUser, result.Value?.Authentication?.User.Role);
        Assert.Equal(new[] { "UpdateLoginAsync", "AddRefreshTokenAsync", "WriteAuditAsync" }, saved);
    }
    private sealed class Transaction : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Invalid_or_revoked_identity_returns_401_without_database_access()
    {
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => throw new GoogleIdentityException(GoogleIdentityFailure.InvalidToken));
        var store = ResetProxy.For<IAuthStore>((_, _) => throw new Exception("Must not access DB"));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs())
            .Handle(new("token"), default);
        Assert.Equal(401, result.Error?.Status);
        Assert.Equal("INVALID_FIREBASE_TOKEN", result.Error?.Code);
    }

    [Fact]
    public async Task Account_disabled_while_waiting_for_lock_cannot_receive_a_session()
    {
        var user = new User { Id = Guid.NewGuid(), FirebaseUid = "uid", Email = "user@example.test", Role = UserRole.Trainee, IsActive = true };
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(user),
            "BeginUserTransactionAsync" => Disable(),
            _ => throw new Exception("Must not issue session: " + method)
        });
        Task<IAuthTransaction> Disable() { user.IsActive = false; return Task.FromResult<IAuthTransaction>(new Transaction()); }
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity("uid", user.Email)));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs()).Handle(new("token"), default);
        Assert.Equal(403, result.Error?.Status);
        Assert.Equal("ACCOUNT_DISABLED", result.Error?.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_uid_requires_onboarding_or_explicit_link_without_mutation(bool emailExists)
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(null),
            "FindUserByEmailAsync" => Task.FromResult<User?>(emailExists ? new User() : null),
            _ => throw new Exception("Must not create/link accounts: " + method)
        });
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity("new-uid", "user@example.test")));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs()).Handle(new("token"), default);
        if (emailExists) { Assert.Equal(409, result.Error?.Status); Assert.Equal("ACCOUNT_LINK_REQUIRED", result.Error?.Code); }
        else Assert.Equal("OnboardingRequired", result.Value?.Status);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("network")]
    public async Task Provider_failure_returns_503_without_looking_up_an_account(string failure)
    {
        var provider = ResetProxy.For<IIdentityProvider>((_, _) =>
        {
            if (failure == "timeout") throw new TimeoutException();
            throw new HttpRequestException();
        });
        var store = ResetProxy.For<IAuthStore>((_, _) => throw new Exception("Must not access DB"));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs())
            .Handle(new("token"), default);
        Assert.Equal(503, result.Error?.Status);
        Assert.Equal("GOOGLE_PROVIDER_UNAVAILABLE", result.Error?.Code);
    }

    [Fact]
    public async Task Uid_remapped_during_lock_wait_does_not_mutate_the_new_owner()
    {
        var before = new User { Id = Guid.NewGuid(), FirebaseUid = "google-uid", Email = "user@example.test", Role = UserRole.Trainee, IsActive = true };
        var after = new User { Id = Guid.NewGuid(), FirebaseUid = before.FirebaseUid, Email = before.Email, Role = before.Role, IsActive = true };
        var reads = 0;
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(reads++ == 0 ? before : after),
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction()),
            _ => throw new Exception("Must not mutate another identity: " + method)
        });
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity(before.FirebaseUid, before.Email)));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs())
            .Handle(new("token"), default);
        Assert.Equal("ACCOUNT_CHANGED", result.Error?.Code);
    }

    [Fact]
    public async Task Linked_google_identity_does_not_bypass_pending_account_verification()
    {
        var user = new User { Id = Guid.NewGuid(), FirebaseUid = "google-uid", Email = "user@example.test", Role = UserRole.Trainee,
            IsActive = true, RegistrationExpiresAt = DateTime.UtcNow.AddHours(1) };
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(user),
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction()),
            _ => throw new Exception("Must not issue session: " + method)
        });
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity(user.FirebaseUid, user.Email)));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var result = await new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs())
            .Handle(new("token"), default);
        Assert.Equal("EMAIL_NOT_VERIFIED", result.Error?.Code);
        Assert.Equal(403, result.Error?.Status);
    }
}
