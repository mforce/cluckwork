namespace Cluckwork.Application.Modules.Access.Contracts;

// #364/#857 — the credential check CredentialEpochMiddleware runs on every
// authenticated request. Each call is one fresh database read: that round trip
// IS the fail-closed revocation guarantee. An implementation must not cache,
// memoise or share a result between calls, not even with an overlapping call,
// and a failed read must throw rather than return a verdict.
// CredentialEpochFreshReadTests exercises each of those rules on the paths it
// drives; it cannot prove them for every implementation, so review still must.
public interface ICredentialEpochVerifier
{
    // connectedApp: the credential is an OAuth token, which the farm's
    // connected-apps switch also governs (#1146).
    Task<CredentialCheck> VerifyAsync(
        Guid userId, Guid accountId, int tokenEpoch, bool connectedApp, CancellationToken ct = default);
}

// #1281 — the verdict and, from the same read, the two columns the Owner
// two-factor gate needs, so the gate adds no query. CredentialEpochMiddleware
// hands it on as a request feature on Current only.
public readonly record struct CredentialCheck(
    CredentialVerdict Verdict, bool TwoFactorEnabled, DateTimeOffset? SecondFactorRequiredSince);

// #1281 (#1274 decision 18) — sets an Owner's grace start, once: a null
// SecondFactorRequiredSince becomes now. Never moves a value already set.
public interface IOwnerGraceStart
{
    Task StartAsync(Guid accountId, Guid userId, DateTimeOffset now, CancellationToken ct);
}

// Zero is deliberately not Current: a default(CredentialVerdict), whether from a
// swallowed exception, a test double or an uninitialised local, must refuse.
public enum CredentialVerdict
{
    None = 0,
    Current = 1,
    UnknownUser,
    Disabled,
    FarmSuspended,
    ConnectedAppsOff,
    Superseded,
}
