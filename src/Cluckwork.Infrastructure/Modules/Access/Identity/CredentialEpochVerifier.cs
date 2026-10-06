using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #364/#857 — the read behind CredentialEpochMiddleware. Scoped and stateless;
// see ICredentialEpochVerifier for why it must stay that way.
public sealed class CredentialEpochVerifier(AppDbContext db) : ICredentialEpochVerifier
{
    public async Task<CredentialVerdict> VerifyAsync(
        Guid userId, Guid accountId, int tokenEpoch, CancellationToken ct = default)
    {
        // #532 — Account.IsActive folds into the user read as a correlated
        // subquery: one round trip, not two. This is what makes suspension
        // immediate rather than "effective at token expiry" (epic #530 decision
        // 15), so it is enforcement, not a nicety — do not delete it as cosmetic.
        //
        // IgnoreQueryFilters is DEFENSIVE, not required: it makes this read
        // independent of TenantResolutionMiddleware having run. Today nothing
        // depends on that — TenantResolutionMiddleware resolves the tenant from
        // the SAME account_id claim the middleware passes here, so
        // tenant.AccountId == accountId and the filter matches. Keep it anyway: a
        // read whose correctness does not hinge on middleware order is worth
        // keeping. No test claims to cover this.
        //
        // `Account` is named so the module ledger sees this read of Farm's table
        // (the Access -> Farm edge, #850).
        var credentialState = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.AccountId == accountId)
            .Select(user => new
            {
                user.CredentialEpoch,
                user.DisabledAt,
                AccountIsActive = db.Accounts.IgnoreQueryFilters()
                    .Where((Account account) => account.Id == user.AccountId)
                    .Select(account => (bool?)account.IsActive)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        // #532 — PRECEDENCE IS DELIBERATE, and it is the reason the epoch test
        // comes last. Suspending a farm bumps every one of its users'
        // CredentialEpoch, so a suspended farm's bearer fails BOTH the account
        // test and the epoch test. Checking the epoch first would answer
        // Auth.CredentialsSuperseded — "sign in again" — to someone whose farm is
        // suspended and whose sign-in cannot succeed. Order: unknown user, then
        // disabled user, then suspended farm, then epoch.
        if (credentialState is null)
            return CredentialVerdict.UnknownUser;
        if (credentialState.DisabledAt is not null)
            return CredentialVerdict.Disabled;
        // `!= true`, not `== false`: a missing account row reads as null, and
        // null must not pass.
        if (credentialState.AccountIsActive != true)
            return CredentialVerdict.FarmSuspended;
        // #1031 — the floor keeps a missing or malformed claim, parsed as 0, a
        // mismatch even when the stored row is 0 or negative.
        return credentialState.CredentialEpoch >= 1 && credentialState.CredentialEpoch == tokenEpoch
            ? CredentialVerdict.Current
            : CredentialVerdict.Superseded;
    }
}
