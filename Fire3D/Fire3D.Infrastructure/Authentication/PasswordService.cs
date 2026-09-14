using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Authentication;

public sealed class PasswordService : IPasswordService
{
    // Identity V3 uses salted PBKDF2-HMAC-SHA512. Store the versioned hash, not plaintext.
    private readonly PasswordHasher<User> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 220_000
    }));
    private readonly User dummy = new();
    private readonly string dummyHash;

    public PasswordService() => dummyHash = hasher.HashPassword(dummy, Guid.NewGuid().ToString());
    public string Hash(User user, string password) => hasher.HashPassword(user, password);
    public bool Verify(User user, string password, out bool needsRehash)
    {
        PasswordVerificationResult result;
        try { result = hasher.VerifyHashedPassword(user, user.PasswordHash, password); }
        catch (FormatException) { result = PasswordVerificationResult.Failed; }
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
    public void VerifyDummy(string password) => hasher.VerifyHashedPassword(dummy, dummyHash, password);
}
