using Cluckwork.Api.Validation;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using FluentValidation;

namespace Cluckwork.Api.Modules.Access.Auth;

public static partial class AuthEndpoints
{
    // #283 follow-up (#361) — the error code a failed sign-in carries when the
    // DEFAULT ACCOUNT has no Owner, so the SPA can explain the dead end instead
    // of showing the generic denial. Rides the ProblemDetails `title`, like
    // every other login error code. Scoped to that account, not to the instance
    // — see the Login handler for what that does and does not claim.
    public const string NoOwnerProvisionedCode = "Auth.NoOwnerProvisioned";

    // #532 — the farm code names a farm this deployment does not have. Epic #530
    // decision 6 chose a DISTINCT response knowingly: it makes /auth/login a
    // farm-enumeration oracle, and that was accepted because the alternative
    // (a generic denial) leaves an operator who mistyped their farm code with no
    // way to tell that from a wrong password. The slug MUST NOT reach a metric
    // label or a log dimension — it is attacker-supplied and unbounded.
    public const string UnknownFarmCodeCode = "Auth.UnknownFarmCode";

    // #532 — the farm exists and is suspended. DISTINCT from invalid credentials
    // by owner decision, AMENDING epic #530 decision 6, which required suspended
    // and active farms to be indistinguishable. The trade was made explicitly:
    // telling a farm's own staff their password is wrong is a lie that generates
    // support load, and farm existence is already disclosed by the branch above.
    public const string FarmSuspendedCode = "Auth.FarmSuspended";

    private static async Task<IResult> Login(
        LoginRequest request, IAccessModule access, IValidator<LoginRequest> validator,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        // #309 — reject an OVERSIZED email/password (400) before the hasher. An
        // empty/short credential is NOT rejected here: it still flows to
        // LoginAsync and returns the generic non-enumerating 401 (unchanged).
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        // #532 — resolve the FARM before the credential. The lookup folds the
        // code to lowercase and ignores the tenant query filter; both are
        // mandatory, not tidiness. Login is AllowAnonymous, so TenantContext is
        // unresolved and the Account filter (AccountId == Guid.Empty) matches
        // zero rows — a lookup written the obvious way reports every farm code as
        // unknown, silently.
        var farm = await access.ResolveFarmCodeAsync(request.FarmCode, ct);
        if (farm is null)
        {
            // Identity-free, exactly like every other unsuccessful branch, and
            // carrying NO slug: this stream must not become the enumeration
            // oracle the response already is, at unbounded log cardinality.
            // Deliberately does NOT touch AccessFailedAsync — there is no user
            // row here, and a farm code must never burn a real account's
            // lockout budget.
            access.RecordLoginFailure();
            return Results.Problem(
                "That farm code is not recognised.",
                statusCode: 401, title: UnknownFarmCodeCode);
        }

        if (!farm.IsActive)
        {
            // BEFORE the credential check and before the first-run notice below:
            // a suspended farm must answer the same way whether or not the
            // password was right, and must not fall through to a branch that
            // discloses its provisioning state instead.
            access.RecordLoginFailure();
            return Results.Problem(
                "This farm is suspended. Contact your administrator.",
                statusCode: 401, title: FarmSuspendedCode);
        }

        var result = await access.LoginAsync(farm.AccountId, request.Email, request.Password, ct);
        if (!result.IsSuccess)
        {
            // #283 follow-up (#361) — first-run discoverability, reported HERE
            // rather than from an endpoint the login screen polls. A freshly
            // migrated instance has base reference data but no administrator,
            // because no credential is ever migration-baked, so there is nobody
            // for the operator to sign in as and the form used to say nothing
            // about why.
            //
            // Deliberately on the FAILURE path only, and only while the DEFAULT
            // ACCOUNT has no Owner — that account and no other, which is the
            // scope every sentence below is held to:
            //
            //  * It cannot ENUMERATE. The condition is a property of the default
            //    account and never of the address that was typed, so no attempt
            //    reveals anything about any particular account. Once the default
            //    account has an Owner this branch is unreachable and the response
            //    is byte-identical to the generic denial it has always been.
            //    Scoped to the default account, NOT to "any Owner anywhere": an
            //    Owner under a different account leaves this reachable, which
            //    FirstRunLoginNoticeTests sets up and asserts deliberately (it
            //    pins the AccountId predicate), so the wider claim would have
            //    been false in a state this suite already exercises — round 5
            //    caught it there.
            //  * It DOES disclose one fact to an anonymous caller who attempts a
            //    sign-in: the default account has no Owner. Stated plainly
            //    because an earlier version of this comment claimed the
            //    condition "can only hold while no credential exists to protect",
            //    which is false (PR #363 review round 2) — the predicate is the
            //    absence of an OWNER, and the seeders create Workers/Managers
            //    without one, so real protected credentials can exist while this
            //    fires. Accepted: the fact is not itself a credential and grants
            //    no access, it is already inferable by anyone who can reach the
            //    form on a fresh install, and unlike the status endpoint this
            //    replaced it reaches only someone who actually attempted to sign
            //    in. The cost of withholding it is an operator stranded at a
            //    form no credential of theirs can satisfy.
            //  * Only someone actually attempting to sign in learns it — unlike
            //    a status endpoint, which answers anyone who asks without them
            //    trying.
            //  * The cost rides on a request that is already rate limited (#143)
            //    and already ran a PBKDF2 verify, so one indexed EXISTS is
            //    noise; and past first provisioning FirstRunProvisioningLatch
            //    makes it a memory read, so the steady state adds no query at
            //    all.
            //
            // A successful sign-in never reaches this, which is what keeps the
            // check off the hot path.
            // #532 (owner decision, AMENDING this issue's body, which asked for a
            // per-account hint). Kept scoped to the DEFAULT account: making it
            // per-account would answer "does farm X have an Owner yet?" for any
            // farm code an attacker can guess, and would re-open the DoS the
            // latch closed, since a farm that is never provisioned never latches
            // and every failed login re-runs the triple-nested query.
            if (farm.AccountId == SeedDefaults.AccountId && !await access.IsFirstRunProvisionedAsync(ct))
                // Says ADMINISTRATOR, not "no accounts" and not "no sign-in can
                // succeed" (PR #363 review). The predicate is specifically "the
                // default account has no Owner", which is #283's provisioning
                // invariant — and a non-Owner user can exist without one (the
                // seeders create them, and this suite's own
                // ASuccessfulSignIn_NeverReportsIt sets up exactly that state).
                // Such a user signs in perfectly well, so the broader claim
                // would be false in a reachable state.
                return Results.Problem(
                    "This farm has no administrator account yet — first-time setup has not been "
                    + "completed. Whoever set up this server must create the first administrator.",
                    statusCode: 401,
                    title: NoOwnerProvisionedCode);

            return Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
        }

        // #532 — per-farm cookie: this login writes the cookie for the farm
        // that was just authenticated and can no longer disturb any other
        // farm's cookie. The old last-login-wins-over-every-tab behaviour is
        // gone by construction, not by a guard.
        AuthCookies.SetRefreshCookie(response, farm.AccountId, result.Value.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }
}
