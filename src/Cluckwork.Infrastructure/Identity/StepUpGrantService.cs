namespace Cluckwork.Infrastructure.Identity;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// #308/#360 — THREAT MODEL + MECHANISM (owner-decided; see issues #308 and
// #360).
//
// Threat: a stolen-but-still-valid Owner ACCESS TOKEN (bearer, ~15 min TTL by
// default — JwtOptions.AccessTokenMinutes — with no server-side denylist) is,
// on its own, enough to create ANY user with an attacker-chosen password,
// reset ANY user's password, or change ANY user's role — each of which mints
// or widens a durable credential whose lifetime exceeds the token's own.
// Neither the created role nor the target's role excuses the proof. #283's
// first-run provisioning and #265's break-glass `recover-admin` CLI verb solve
// different problems (initial setup; a locked-out sole Owner with server
// access) and are untouched by this — break-glass in particular stays a CLI
// verb, never reachable from this browser flow.
//
// Mechanism: current-password re-confirmation. POST /auth/step-up mints a
// SEPARATE, short-lived JWT — the "step-up grant" — that the caller presents
// (as the X-Cluckwork-Step-Up header, never the request body) alongside the
// normal Bearer access token on the three gated calls — every CreateUser
// regardless of the created role, every SetUserPassword regardless of the
// target's role, and every ChangeUserRole (#355) regardless of the requested
// role.
// Properties, and how each failure mode is produced:
//
//   - Lifetime: JwtOptions.StepUpGrantMinutes (default 5) from issuance —
//     long enough to finish one confirm-password step and fire the one
//     sensitive request it unlocks; short enough to bound what a captured
//     grant is worth. EXPIRED → same denial as every other rejection below.
//   - Audience-limited: aud="cluckwork-step-up", a literal never sourced
//     from config, so it can never collide with the real API audience. A
//     captured grant presented as a normal Bearer token is rejected by the
//     standard JWT-bearer handler's own audience check before it ever reaches
//     application code — "unusable as a normal access token" is enforced by
//     the SAME mechanism that already protects every other endpoint, not by
//     bespoke logic here.
//   - Bound to account + user: sub/account_id claims are compared against the
//     CALLER's own resolved identity (from their access token/TenantContext)
//     on every use. A mismatch (a grant minted for a different account, or —
//     structurally impossible for one user to belong to two accounts, but
//     checked regardless — a different user) → WRONG-ACCOUNT denial.
//   - Single-use: the jti is consumed via IStepUpGrantRegistry on the first
//     successful validation. A second presentation of the same token →
//     REPLAYED denial, even before its natural expiry. Consumption is not a
//     step of its own: it is fused with the logout check below into one atomic
//     registry call (TryConsumeIfNotLoggedOut) — see the next bullet.
//   - Revoked by credential rotation: the grant embeds the user's
//     SecurityStamp at issuance. ASP.NET Identity rotates SecurityStamp on
//     every password change/reset (ChangePasswordAsync/ResetPasswordAsync),
//     so a grant issued before such a change no longer matches the user's
//     CURRENT stamp → REVOKED denial.
//   - Revoked by logout: each grant embeds the user's step-up logout epoch
//     (LogoutEpochClaim) as it was when the grant was issued; logout advances
//     that epoch; a grant whose embedded epoch no longer equals the user's
//     current one is refused even if unexpired and unused, so a grant captured
//     before a legitimate logout cannot be used after it → REVOKED denial. An
//     integer compared for equality — never a wall-clock timestamp — so the
//     check is immune to the clock skew between the replica that issued the
//     grant and the one that recorded the logout (#338 review).
//     AuthEndpoints.Logout records the logout along TWO independent axes,
//     because the credentials a logout presents need not name one user: the
//     account header selects a per-farm refresh COOKIE while the SPA holds each
//     ACCESS TOKEN in its own tab's memory. So the cookie
//     owner is recorded via IdentityProvider.RevokeRefreshTokenAsync, AND the
//     authenticated bearer's subject via RecordLogoutAsync. Deriving the owner
//     from the cookie alone (as this shipped) meant a user logging out when the
//     selected cookie named someone else — or was missing/expired — kept their
//     grant alive, which is precisely
//     the person this guarantee is for (#336 review). Neither axis revokes the
//     other's refresh tokens: ending one tab's session must not sign the user
//     out of every device. A missing or unparseable epoch claim FAILS CLOSED
//     into the same denial — it is never guessed.
//     ATOMICITY (#336 review, 3rd round): this check and the single-use
//     consumption above are ONE registry call, not two. As two calls — even
//     with each individually thread-safe — a logout could complete in the gap
//     between them, so a validation already past the epoch check went on to
//     consume its jti and succeed with a grant the logout had just revoked.
//     No lock can span the two stores the registry now uses, so the guarantee
//     is re-established by ORDERING inside that call — consume the claim
//     first, then read the epoch. The full trace, and the rule against
//     re-splitting or re-ordering them, are on
//     IStepUpGrantRegistry.
//   - Non-enumerating failure handling: every one of the above rejection
//     reasons — missing, malformed, expired, replayed, wrong-account/user,
//     stamp-revoked, logout-revoked — maps to the SAME error
//     (StepUpErrorCodes.Required) on the gated endpoints. A caller (or an
//     attacker probing) cannot tell WHY a grant failed. /auth/step-up's OWN
//     failure (wrong current password) is allowed to say so plainly: the
//     caller is already a specific authenticated user re-confirming their
//     OWN credential, so there is nothing left to enumerate — this mirrors
//     ChangeOwnPasswordAsync's "Users.CurrentPasswordIncorrect", and (#336
//     review) carries that flow's 400 rather than a 401: the SPA's apiFetch
//     reads every 401 as an expired access token and transparently refreshes
//     and REPLAYS the request, which would double-count each wrong password
//     against the per-account lockout below. 401 stays reserved for a caller
//     whose session genuinely isn't there.
//   - Rate-limited like login/change-password (the endpoint opts into
//     RateLimitingOptions.LoginPolicyName): an attacker holding a stolen
//     access token still cannot brute-force the password behind unlimited
//     attempts.
//   - Browser storage: the raw grant token lives ONLY in transient JS state
//     on the page that requested it (web/src/routes/UsersPage.tsx) for the
//     single call that consumes it — never localStorage/sessionStorage, and
//     never a longer-lived module-level store (unlike the access token,
//     which the SPA does keep in memory across the tab's lifetime — a step-up
//     grant is single-use, so keeping it around after its one use would buy
//     nothing). The password used to obtain it lives in a controlled input
//     only and is cleared the instant the /auth/step-up call settles
//     (success OR failure); it is sent to that one request and nowhere else,
//     and never persisted.
//   - Storage (#338): replay protection lives in the shared IClaimOnceStore
//     (#543) and logout revocation in the durable ApplicationUser.StepUpLogoutEpoch
//     column (an integer epoch the grant embeds at issue and validation compares
//     for equality). The grant's own signed claims still carry sub/jti/account/
//     security_stamp/epoch; the registry (IStepUpGrantRegistry) is now a thin,
//     stateless facade over those two stores.
//
// Explicitly deferred: TOTP/WebAuthn step-up (tracked as #320) — this PR is
// password-re-confirmation only.
public sealed class StepUpGrantService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    IStepUpGrantRegistry registry,
    AuthSecurityEventLogger securityEvents) : IStepUpGrantService
{
    // Never sourced from config — see the threat-model note above.
    internal const string Audience = "cluckwork-step-up";

    // The user's step-up logout epoch at the instant this grant was issued
    // (invariant decimal int). Logout revocation compares this against the user's
    // CURRENT epoch — an equality check on an integer, never a wall clock, so it
    // is immune to cross-replica clock skew (#338 review). Inside the signed
    // payload, so it is integrity-protected like every other claim.
    internal const string LogoutEpochClaim = "stepup_logout_epoch";

    // #309-style defensive cap ahead of parsing: a real grant is a compact JWT
    // a few hundred bytes long: 2048-bit RSA signature + a handful of short
    // claims comfortably fits; 4096 rejects a pathological header before it
    // reaches the JWT handler.
    private const int MaxTokenLength = 4096;

    private static readonly Error DeniedError = Error.Validation(
        StepUpErrorCodes.Required, "Recent re-authentication is required for this action.");

    private static readonly Error WrongPasswordError =
        Error.Validation("Users.CurrentPasswordIncorrect", "Current password is incorrect.");

    public async Task<Result<StepUpGrant>> IssueAsync(
        Guid accountId, Guid userId, string currentPassword, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.AccountId != accountId)
        {
            // Always pay the PBKDF2 cost, exactly like LoginAsync, so a
            // missing/foreign user isn't measurably faster than a real check.
            userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), TimingEqualization.DummyHash, currentPassword);
            // #273 codex review (P1b) — this is a SECOND password oracle
            // (LoginAsync being the first); SecurityEvents.LoginFailed's own
            // doc already says it fires on /auth/login OR /auth/step-up, on
            // all three unsuccessful branches, with the identical field set.
            securityEvents.LoginFailed();
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        if (user.DisabledAt is not null)
        {
            // Match login's disabled-account path: pay exactly one real hash
            // cost for either password, but never let guesses mutate durable
            // failed-access/lockout state while the account is disabled.
            userManager.PasswordHasher.VerifyHashedPassword(
                user, user.PasswordHash ?? TimingEqualization.DummyHash, currentPassword);
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        // #128 per-account lockout, same as LoginAsync. This endpoint is a SECOND
        // password oracle — and the one guarding Owner takeover — so leaving it
        // out would have left only the per-IP limiter here, which a distributed
        // attacker rotating source IPs walks straight around. A locked account is
        // refused with the same error and still pays PBKDF2, so the reply never
        // reveals that the account is locked rather than the password wrong.
        if (await userManager.IsLockedOutAsync(user))
        {
            userManager.PasswordHasher.VerifyHashedPassword(
                user, user.PasswordHash ?? TimingEqualization.DummyHash, currentPassword);
            // #273 codex review (P1b) — same as LoginAsync's already-locked
            // branch: re-fires LoginFailed, never AccountLockedOut again (that
            // fired once, at the transition).
            securityEvents.LoginFailed();
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        if (!await userManager.CheckPasswordAsync(user, currentPassword))
        {
            // #273 codex review (P1b) — the bool this returns was previously
            // discarded, so a failed step-up attempt never fired LoginFailed
            // and a threshold-crossing one never fired AccountLockedOut: a
            // failed /auth/step-up — a privileged password oracle — was
            // invisible to security telemetry. Same shape as LoginAsync's
            // wrong-password branch.
            //
            // Emitted BEFORE persisting lockout state (codex review round 2):
            // RecordFailedAccessAsync is a durable write that can throw, and a
            // throw there must not silently drop LoginFailed — the password was
            // already confirmed wrong.
            securityEvents.LoginFailed();
            var justLockedOut = await AccountLockout.RecordFailedAccessAsync(userManager, db, user);
            if (justLockedOut)
                securityEvents.AccountLockedOut(user.Id);
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        // ResetFailedAccessCountAsync can reload a concurrently reset/disabled
        // row after an optimistic-concurrency loss. Preserve the exact
        // credential proof we just accepted so the old password cannot issue a
        // grant stamped with credentials that superseded it.
        var verifiedCredentialEpoch = user.CredentialEpoch;
        var verifiedSecurityStamp = user.SecurityStamp;

        // Correct password — clear any accumulated failures, as login does.
        // Shared with login via AccountLockout (#269 review): a discarded
        // IdentityResult here left `user` tracked with a stale ConcurrencyStamp,
        // which any later save on this scoped DbContext would re-flush and throw.
        await AccountLockout.ResetFailedAccessCountAsync(userManager, db, user, ct);
        if (user.DisabledAt is not null
            || user.CredentialEpoch != verifiedCredentialEpoch
            || !string.Equals(user.SecurityStamp, verifiedSecurityStamp, StringComparison.Ordinal))
        {
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(Math.Max(1, jwtOptions.Value.StepUpGrantMinutes));
        var jti = Guid.NewGuid();

        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(jwtOptions.Value.PrivateKeyPem));
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti.ToString()),
            new("account_id", accountId.ToString()),
            // Embeds the CURRENT stamp so any later rotation (password
            // change/reset) invalidates this grant — see the class comment.
            new("security_stamp", user.SecurityStamp ?? string.Empty),
            // The user's step-up logout epoch NOW; logout advances it and this
            // grant is refused once it no longer matches. Read from the row this
            // method already validated. If a logout increments it between here and
            // first use, the grant is refused on use — the fail-safe direction.
            new(LogoutEpochClaim, user.StepUpLogoutEpoch.ToString(CultureInfo.InvariantCulture)),
        };

        var token = new JwtSecurityToken(
            jwtOptions.Value.Issuer, Audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);
        var raw = new JwtSecurityTokenHandler().WriteToken(token);

        return Result.Success(new StepUpGrant(raw, expires));
    }

    public async Task<Result> ValidateAsync(
        Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(stepUpToken) || stepUpToken.Length > MaxTokenLength)
            return Result.Failure(DeniedError);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(jwtOptions.Value.PublicKeyPem));

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Value.Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            // CacheSignatureProviders = false, exactly as IssueAsync does for
            // the signing side, and for the same reason — found while adding
            // the atomic-admission test below, but a defect in its own right.
            // `rsa` is disposed when this method returns, while the DEFAULT
            // CryptoProviderFactory caches the verifying SignatureProvider
            // process-wide, keyed on the KEY MATERIAL. Two validations close
            // enough together therefore hand the second one a cached provider
            // still holding the FIRST call's now-disposed RSA, and the token
            // fails with SecurityTokenSignatureKeyNotFoundException — i.e. a
            // perfectly good grant is refused because an unrelated grant was
            // validated moments earlier. It fails CLOSED, so it was never a
            // security hole, but it is a real and timing-dependent denial of
            // a legitimate step-up; the key is loaded from config on every
            // call anyway, so there is nothing for the cache to save here.
            //
            // It also mattered for the single-use guarantee's test coverage:
            // a replay's SECOND validation is precisely a back-to-back pair,
            // so CreateOwner_ReplayedStepUp_SecondUseIs403 could be satisfied
            // by this spurious signature failure instead of by the registry
            // actually refusing the jti — green for the wrong reason.
            IssuerSigningKey = new RsaSecurityKey(rsa)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            },
            // Expiry is enforced MANUALLY below against the injected
            // TimeProvider rather than the wall clock — the same convention
            // refresh tokens already use (IdentityProvider.RefreshAsync) — so
            // tests can move time deterministically instead of sleeping.
            ValidateLifetime = false,
        };

        JwtSecurityToken jwt;
        try
        {
            new JwtSecurityTokenHandler().ValidateToken(stepUpToken, parameters, out var validated);
            jwt = (JwtSecurityToken)validated;
        }
        catch (SecurityTokenException)
        {
            // Malformed, wrong audience/issuer, or a bad/foreign signature —
            // all collapse to the same non-enumerating denial.
            return Result.Failure(DeniedError);
        }
        catch (ArgumentException)
        {
            // Not even parseable as a JWT.
            return Result.Failure(DeniedError);
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        if (expiresAt <= now)
            return Result.Failure(DeniedError); // expired

        var sub = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
        var acct = jwt.Claims.FirstOrDefault(c => c.Type == "account_id")?.Value;
        var jtiClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        var stamp = jwt.Claims.FirstOrDefault(c => c.Type == "security_stamp")?.Value;
        var epochClaim = jwt.Claims.FirstOrDefault(c => c.Type == LogoutEpochClaim)?.Value;
        // NumberStyles.None: digits only — no sign, whitespace or separators. A
        // missing or non-numeric epoch collapses into the same malformed-claims
        // denial rather than being guessed.
        if (sub != userId.ToString() || acct != accountId.ToString()
            || !Guid.TryParse(jtiClaim, out var jti) || stamp is null
            || !int.TryParse(epochClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var grantEpoch))
            return Result.Failure(DeniedError); // wrong-account / wrong-user / malformed claims

        // Validation only compares persisted grant-binding fields. Keep this
        // read out of the shared scoped context's identity map: a privileged
        // handler may wait on an account lock after validation and must not
        // identity-resolve this pre-lock row as its mutation target.
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, ct);
        if (user is null || user.AccountId != accountId || user.SecurityStamp != stamp)
            return Result.Failure(DeniedError); // revoked by a security-stamp change

        // ONE registry call, not two — the logout-revocation check and the
        // single-use consumption are ONE indivisible decision. Splitting them lets
        // a concurrent logout land between them and be missed by a validation
        // already in flight; the full race trace is on IStepUpGrantRegistry, and
        // the calls this gate protects are exactly the ones that hand out durable
        // account control. A single bool comes back on purpose: "revoked by logout"
        // and "replayed" are the same non-enumerating denial.
        if (!await registry.TryConsumeIfNotLoggedOutAsync(userId, jti, grantEpoch, expiresAt, now, ct))
            return Result.Failure(DeniedError); // revoked by logout, or replayed

        return Result.Success();
    }
}
