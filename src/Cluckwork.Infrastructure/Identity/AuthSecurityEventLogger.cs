namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// #273 codex review (P1b) — single emission point for the LoginFailed /
// AccountLockedOut security events, shared by EVERY password-verification
// oracle: IdentityProvider.LoginAsync AND StepUpGrantService.IssueAsync
// (#308's Owner-takeover re-confirmation, a SECOND password oracle). Before
// this existed, the emit logic (event name, {ClientIp} shape, when
// AccountLockedOut is allowed to fire) lived only as a private method on
// IdentityProvider, so StepUpGrantService had no way to reach it short of
// hand-copying the two LogWarning call sites — exactly the kind of second
// copy AccountLockout.cs's own class comment warns is "a second thing to
// forget to update." Centralizing here means both oracles stay byte-for-byte
// identical in what they log, not just similar.
public sealed class AuthSecurityEventLogger(
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuthSecurityEventLogger> logger)
{
    // Resolved per-call, never cached: this type is registered scoped (one
    // instance per request), but reading the accessor lazily here — rather
    // than in the constructor — keeps it honest for a hypothetical future
    // caller with no ambient HttpContext (null -> "unknown", never a throw).
    private string ClientIp =>
        httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Fires on EVERY unsuccessful password check on either oracle — unknown
    // email/user, an already-locked account, or a genuinely wrong password —
    // with the IDENTICAL field set every time. Deliberately carries NO user
    // id or email: see SecurityEvents.LoginFailed for why a caller must never
    // add one here.
    public void LoginFailed() =>
        logger.LogWarning("{SecurityEvent} client={ClientIp}", SecurityEvents.LoginFailed, ClientIp);

    // Callers MUST pass only a transition (AccountLockout.RecordFailedAccessAsync's
    // own return value — true exactly once per lockout episode), never call
    // this unconditionally on every failed attempt against an already-locked
    // account. Safe to name the user on: unlike LoginFailed, this never fires
    // on an unknown-email branch, so its presence alone can't be used to tell
    // a nonexistent email apart from a wrong password for a real one.
    public void AccountLockedOut(Guid userId) =>
        logger.LogWarning("{SecurityEvent} user={UserId} client={ClientIp}",
            SecurityEvents.AccountLockedOut, userId, ClientIp);
}
