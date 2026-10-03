namespace Cluckwork.Application.Features.Users;

// #364/#857 — the credential check CredentialEpochMiddleware runs on every
// authenticated request. Each call is one fresh database read: that round trip
// IS the fail-closed revocation guarantee. An implementation must not cache,
// memoise or share a result between calls, and a failed read must throw rather
// than return a verdict. CredentialEpochFreshReadTests pins both.
public interface ICredentialEpochVerifier
{
    Task<CredentialVerdict> VerifyAsync(
        Guid userId, Guid accountId, int tokenEpoch, CancellationToken ct = default);
}

public enum CredentialVerdict
{
    Current,
    UnknownUser,
    Disabled,
    FarmSuspended,
    Superseded,
}
