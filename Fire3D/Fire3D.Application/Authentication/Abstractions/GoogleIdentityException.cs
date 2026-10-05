namespace Fire3D.Application.Authentication.Abstractions;

public enum GoogleIdentityFailure { InvalidToken, ProviderUnavailable }

// Fixed messages only: SDK exceptions can include request/token details.
public sealed class GoogleIdentityException(GoogleIdentityFailure failure)
    : Exception(failure == GoogleIdentityFailure.InvalidToken ? "Invalid Google identity." : "Google verification is temporarily unavailable.")
{
    public GoogleIdentityFailure Failure { get; } = failure;
}
