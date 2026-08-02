namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #283 follow-up — first-run DISCOVERABILITY. The provisioning mechanism itself
// (FirstRunAdminService, the `bootstrap-admin` verb) is unchanged; the gap this
// closes is that a freshly migrated instance presents a login form with no
// accounts and no way to learn that, so the operator's first experience is a
// credential prompt that can only ever fail. This answers one question —
// "does the default account have an Owner yet?" — so the SPA can show the
// operator what to run instead of a dead end.
//
// Deliberately NOT a credential path of any kind: it reads existence, never
// identity. No email, no user count, no role list — a bool and nothing else, so
// there is no version of this response that helps someone log in.
//
// Information disclosure, considered and accepted: an anonymous caller learns
// whether an instance is un-provisioned. That state is already inferable (the
// instance answers no valid credential), it is unreachable-by-design after the
// operator's first run (the value latches, see below), and it exposes no secret
// — there is no default credential in this app to go try. The alternative,
// leaving the operator to read the README, is what actually happened and cost a
// debugging session.
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
