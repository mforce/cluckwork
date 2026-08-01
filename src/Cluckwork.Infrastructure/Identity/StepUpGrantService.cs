namespace Cluckwork.Infrastructure.Identity;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// #308 — THREAT MODEL + MECHANISM (owner-decided; see issue #308).
//
// Threat: a stolen-but-still-valid Owner ACCESS TOKEN (bearer, ~15 min TTL by
// default — JwtOptions.AccessTokenMinutes — with no server-side denylist) is
// enough today to (a) create ANOTHER Owner, multiplying durable account
// control past the token's own lifetime, or (b) reset an EXISTING Owner's
// password, taking that account over outright. Neither needs the attacker to
// know any password. #283's first-run provisioning and #265's break-glass
// `recover-admin` CLI verb solve different problems (initial setup; a locked-
// out sole Owner with server access) and are untouched by this — break-glass
// in particular stays a CLI verb, never reachable from this browser flow.
//
// Mechanism: current-password re-confirmation. POST /auth/step-up mints a
// SEPARATE, short-lived JWT — the "step-up grant" — that the caller presents
// (as the X-Cluckwork-Step-Up header, never the request body) alongside the
// normal Bearer access token on the two gated calls (CreateUser with
// Role=Owner; SetUserPassword targeting a user who currently holds Owner).
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
//     REPLAYED denial, even before its natural expiry.
//   - Revoked by credential rotation: the grant embeds the user's
//     SecurityStamp at issuance. ASP.NET Identity rotates SecurityStamp on
//     every password change/reset (ChangePasswordAsync/ResetPasswordAsync),
//     so a grant issued before such a change no longer matches the user's
//     CURRENT stamp → REVOKED denial.
//   - Revoked by logout: IStepUpGrantRegistry also tracks the instant each
//     user last logged out (wired from IdentityProvider.RevokeRefreshToken-
//     Async — the same call AuthEndpoints.Logout already makes). A grant
//     ISSUED AT OR BEFORE that instant is refused even if unexpired and
//     unused, so a grant captured before a legitimate logout cannot be used
//     after it → REVOKED denial. That comparison runs against a DEDICATED
//     tick-precision issuance claim (PreciseIssuedAtClaim), NOT the JWT's own
//     nbf/ValidFrom: nbf is a NumericDate, i.e. floored to whole seconds,
//     while the recorded logout instant keeps sub-second ticks — so a user who
//     logs out at :00.500, signs back in and takes a FRESH grant at :00.800
//     would have that grant read as issued at :00.000, i.e. at-or-before their
//     own logout, and be locked out of the feature until the second rolled
//     over (#336 review). Flooring the stored logout instead would NOT fix it
//     (both collapse to :00.000, and the comparison is at-or-before), and
//     relaxing the comparison to strictly-before would accept a grant
//     genuinely minted earlier in the logout's own second — precisely what
//     this guard exists to refuse. So the issuance instant is carried at full
//     precision inside the SIGNED grant (we mint it, so we control its
//     claims); the standard whole-second nbf/exp are untouched. A missing or
//     unparseable precise claim FAILS CLOSED into the same denial — it never
//     falls back to the floored nbf.
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
//   - No schema change: every guarantee above is carried in the JWT's own
//     claims plus in-process memory (IStepUpGrantRegistry) — no new table,
//     no new column on ApplicationUser. Schema work for this launch batch is
//     owned by a separate concurrent PR; this feature deliberately adds none.
//
// Explicitly deferred: TOTP/WebAuthn step-up (tracked as #320) — this PR is
// password-re-confirmation only.
public sealed class StepUpGrantService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    IStepUpGrantRegistry registry) : IStepUpGrantService
{
    // Never sourced from config — see the threat-model note above.
    internal const string Audience = "cluckwork-step-up";

    // Tick-precision issuance instant (UTC ticks, invariant decimal). Carried
    // ALONGSIDE — never instead of — the standard nbf/exp, which stay
    // whole-second NumericDates. Only the logout-revocation comparison reads
    // it; see the "Revoked by logout" bullet above for why nbf cannot serve.
    internal const string PreciseIssuedAtClaim = "stepup_iat_ticks";

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
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        if (!await userManager.CheckPasswordAsync(user, currentPassword))
        {
            await AccountLockout.RecordFailedAccessAsync(userManager, db, user);
            return Result.Failure<StepUpGrant>(WrongPasswordError);
        }

        // Correct password — clear any accumulated failures, as login does.
        await userManager.ResetAccessFailedCountAsync(user);

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
            // Sub-second issuance instant for the logout comparison. Inside the
            // signed payload, so it is integrity-protected exactly like every
            // other claim here.
            new(PreciseIssuedAtClaim, now.UtcTicks.ToString(CultureInfo.InvariantCulture)),
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
            IssuerSigningKey = new RsaSecurityKey(rsa),
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
        var issuedAtTicks = jwt.Claims.FirstOrDefault(c => c.Type == PreciseIssuedAtClaim)?.Value;
        // NumberStyles.None: digits only — no sign, whitespace or separators.
        // A missing, non-numeric or out-of-range value collapses into the same
        // malformed-claims denial rather than falling back to the floored nbf.
        if (sub != userId.ToString() || acct != accountId.ToString()
            || !Guid.TryParse(jtiClaim, out var jti) || stamp is null
            || !long.TryParse(issuedAtTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return Result.Failure(DeniedError); // wrong-account / wrong-user / malformed claims

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.AccountId != accountId || user.SecurityStamp != stamp)
            return Result.Failure(DeniedError); // revoked by a security-stamp change

        // Tick-precision, NOT jwt.ValidFrom — see the class comment.
        var issuedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (registry.IsRevokedByLogout(userId, issuedAt))
            return Result.Failure(DeniedError); // revoked by logout

        if (!registry.TryConsume(jti, expiresAt, now))
            return Result.Failure(DeniedError); // replayed

        return Result.Success();
    }
}
