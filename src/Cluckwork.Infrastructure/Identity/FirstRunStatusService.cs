namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #283 follow-up — first-run DISCOVERABILITY. The provisioning mechanism itself
// (FirstRunAdminService, the `bootstrap-admin` verb) is unchanged; the gap this
// closes is that a freshly migrated instance has no administrator to sign in as
// and no way for the operator to learn that, so their first experience is a
// credential prompt they cannot satisfy. This answers exactly one question —
// "does the default account have an Owner yet?" — which AuthEndpoints consults
// on a FAILED sign-in so the reply can name the situation instead of blaming
// the credentials.
//
// Read on the login failure path, never from an endpoint of its own. An earlier
// revision exposed it as an anonymous GET the login page polled on mount; that
// answered anyone who asked and reached the database on every anonymous page
// load throughout the window before setup (PR #359 review).
//
// Deliberately NOT a credential path of any kind: it reads existence, never
// identity. No email, no user count, no role list — a bool and nothing else, so
// there is no version of this answer that helps someone log in.
//
// Information disclosure, considered and accepted. Say it precisely, because
// two review rounds were spent on comments that claimed more than this delivers
// (PR #363, rounds 2 and 3):
//
//   * It does not ENUMERATE. The answer depends on the instance and never on
//     any submitted address, so no sign-in attempt reveals anything about any
//     particular account.
//   * It DOES disclose one global fact to an anonymous caller who attempts a
//     sign-in: this instance has no administrator. Note what that does NOT
//     mean — the predicate is the absence of an OWNER, so this can be true
//     while ordinary non-Owner accounts exist, hold valid credentials and sign
//     in perfectly well (the seeders create exactly such users, and
//     FirstRunLoginNoticeTests pins the case). Earlier wording here claimed the
//     state was harmless because "the instance answers no valid credential";
//     that is false in precisely that state.
//
// Accepted anyway: the fact is not itself a credential and grants no access; on
// a genuinely fresh install — no users at all — it is already inferable by
// anyone who can reach the form; it stops being reachable once the first Owner
// exists (the value latches, see below); and there is no default credential in
// this app to go try. The alternative, leaving the operator to find the README,
// is what actually happened and cost a debugging session.
public sealed class FirstRunStatusService(
    AppDbContext db,
    ILookupNormalizer normalizer,
    FirstRunProvisioningLatch latch)
{
    public async Task<bool> IsProvisionedAsync(CancellationToken ct = default)
    {
        // Post-provisioning — the state this instance is in for all but the
        // first few minutes of its life — this is a memory read, so an
        // anonymous endpoint can never be used to make the database do work.
        if (latch.IsProvisioned)
            return true;

        var accountId = SeedDefaults.AccountId;

        // Matched on NormalizedName with Identity's own normalizer rather than
        // on Name, because that is what UserManager.GetUsersInRoleAsync — the
        // definition FirstRunAdminService provisions against — compares. Note
        // Roles.Owner is the string "Admin"; comparing against a hand-written
        // "OWNER" would be silently always-false.
        var ownerRole = normalizer.NormalizeName(Roles.Owner);

        // ApplicationUser carries NO tenant query filter (the 27 filters in
        // AppDbContext are all domain entities), so this is safe on an
        // anonymous request where TenantContext is unresolved. That matters:
        // an unresolved TenantContext has AccountId == Guid.Empty, so a
        // FILTERED entity would match nothing and this would answer
        // "un-provisioned" forever — a green test and a permanently wrong
        // banner. AccountId is compared explicitly here for the same reason
        // FirstRunAdminService does: the filter is not doing it for us.
        var provisioned = await db.Users
            .Where(u => u.AccountId == accountId)
            .AnyAsync(
                u => db.UserRoles.Any(ur =>
                    ur.UserId == u.Id
                    && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == ownerRole)),
                ct);

        if (provisioned)
            latch.Latch();

        return provisioned;
    }
}

// Singleton. "Provisioned" is monotonic in the direction that matters: the
// first Owner is created once and `bootstrap-admin` refuses to create a second,
// so a true observation can never become stale in a way that misleads. Latching
// only on true is deliberate — if every Owner were somehow removed, this keeps
// answering "provisioned" and the SPA simply shows nothing, which is the safe
// direction (no banner is a missing hint; a wrongly-shown banner would tell an
// operator to run a command that would refuse anyway).
public sealed class FirstRunProvisioningLatch
{
    private volatile bool _provisioned;

    public bool IsProvisioned => _provisioned;

    public void Latch() => _provisioned = true;
}
