namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #265 — offline break-glass account recovery. Motivating case: a single-Owner
// farm loses its password and there is no email/SMTP reset path, so the only
// other recourse is direct DB surgery. Invoked by the `recover-admin` CLI
// command (Program.cs), never an HTTP endpoint.
//
// UNLIKE the demo/simulation seeders this is deliberately NOT environment-gated:
// break-glass must work against a real Production database — that is the whole
// point. Its safety comes from requiring shell access to the running deployment
// (to invoke the command at all), and from writing a conspicuous audit row.
public sealed class AdminRecoveryService(
    AppDbContext db,
    TenantContext tenant,
    CurrentUserContext currentUser,
    UserManager<ApplicationUser> userManager,
    IIdentityProvider identity,
    IAccountRepository accounts)
{
    public async Task<Result<AdminRecoveryResult>> RecoverAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.EmailRequired", "An --email is required."));

        // Match on NormalizedEmail, exactly like Identity's login path
        // (FindByEmailAsync) — a case-sensitive `Email ==` compare would miss an
        // account stored as `Owner@Farm.example` when the operator types
        // `owner@farm.example`, returning a spurious NotFound in the one moment
        // the tool must not (#265 review). db.Users carries no tenant query
        // filter, so this works before any tenant is resolved.
        var normalized = email.Trim();
        var normalizedEmail = userManager.NormalizeEmail(normalized);
        var matches = await db.Users
            .Where(u => u.NormalizedEmail == normalizedEmail && (accountId == null || u.AccountId == accountId))
            .Select(u => new { u.Id, u.AccountId, u.Email, u.DisabledAt })
            .ToListAsync(ct);

        if (matches.Count == 0)
            return Result.Failure<AdminRecoveryResult>(Error.NotFound("Recovery", normalized));
        if (matches.Count > 1)
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.Ambiguous",
                $"{matches.Count} users share the email '{normalized}' across accounts — pass --account <id> to disambiguate."));

        var target = matches[0];

        // #356 — refuse a DISABLED target, loudly, instead of handing the
        // operator a password that cannot work. LoginAsync rejects a disabled
        // user before it ever checks the password, so resetting one would print
        // a fresh credential to stdout, write a User.BreakGlassReset audit row
        // and exit 0 for an account that is still locked out: a silent false
        // green in the one tool that exists for an emergency, and a direct
        // breach of the #265 rule that break-glass is fail-loud and never a
        // silent no-op.
        //
        // Refusing is this slice's job; CLEARING DisabledAt here is not — that
        // ships with the --user-id lookup, because locating by email is
        // circular once an email typo is what locked the account. Until then
        // the operator gets an actionable error rather than a lie.
        //
        // The advice ("re-enable them from the Users screen") is followable for
        // any state THIS APPLICATION can produce: the last-active-Owner guard
        // runs inside the account lock on both the disable and the demote path,
        // so no in-app sequence reaches an account with zero active Owners.
        // It is NOT followable for a hand-edited or restored database that
        // arrives with every Owner disabled — and that population is precisely
        // the one break-glass exists for, so the limit is stated rather than
        // assumed away. `bootstrap-admin` is not an escape hatch from it today
        // either: FirstRunAdminService counts Owner ROLE ROWS without excluding
        // DisabledAt, so a disabled Owner still reads as "already provisioned"
        // and it exits 0 having done nothing. Recovering that state needs
        // direct DB surgery until #357 ships --user-id with a clear-disabled.
        // Do not restate this as an unconditional guarantee.
        //
        // THIS FIRST CHECK IS ADVISORY ONLY — a fast, unlocked read so an
        // operator retyping the wrong email gets an answer without paying for
        // a lock. It is NOT what makes the refusal correct; the re-check inside
        // the lock below is (codex review of #492 round 3). Between this read
        // and the reset actually running, another Owner could disable this
        // exact target — without a re-check, recovery would still reset the
        // password, write the audit row, print a credential that cannot work,
        // and exit 0: the identical false-green this whole check exists to
        // prevent, just reached one race window later.
        if (target.DisabledAt is not null)
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.UserDisabled",
                $"'{target.Email}' was disabled at {target.DisabledAt:u}. Resetting the password would NOT restore " +
                "access — a disabled user is refused before the password is checked. Re-enable them from the " +
                "Users screen first, then re-run this command if the password is still unknown."));

        // Resolve the tenant to the target account BEFORE the reset so
        // IAuditWriter (which fails closed on an unresolved tenant) can stamp the
        // break-glass audit row with the correct AccountId.
        tenant.Resolve(target.AccountId);

        // #500 — and the ACTOR, for the same reason: IAuditWriter now fails
        // closed on an unresolved actor too. This command has no signed-in
        // human by design (an operator at a shell during a lockout), so it
        // declares the non-person it is rather than falling into the old
        // "(unresolved)" placeholder. The real accountability for a break-glass
        // reset remains the host + OS user captured in the row's details, plus
        // the operator's --reason.
        currentUser.ResolveSystemActor(SystemActors.BreakGlass);

        // Serialized with the SAME account-wide lock DisableUserAsync takes
        // (IdentityProvider.cs) — not because recovery cares about the
        // last-active-Owner count, but because taking it is what makes a
        // concurrent disable of THIS target block until this transaction
        // commits or rolls back, rather than interleave with it. The re-read
        // of DisabledAt happens INSIDE the lock, so a successful return here
        // is a genuine guarantee the target stayed enabled through the reset,
        // not a repeat of the same race one line closer to the write.
        //
        // `identity.BreakGlassResetAsync` shares this scope's AppDbContext, so
        // its own AmbientTransaction.RunAsync sees CurrentTransaction already
        // set and JOINS this transaction instead of nesting one — the same
        // reentrant contract IdempotencyMiddleware relies on (#307). This
        // whole block is therefore the OWNED unit (recover-admin has no
        // idempotency middleware to wrap it), and runs through
        // SingleAttemptExecution accordingly: never independently replayed.
        return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != target.AccountId)
                return Result.Failure<AdminRecoveryResult>(Error.NotFound("Accounts", target.AccountId));

            var stillDisabledAt = await db.Users.AsNoTracking()
                .Where(u => u.Id == target.Id)
                .Select(u => u.DisabledAt)
                .SingleOrDefaultAsync(token);
            if (stillDisabledAt is not null)
                return Result.Failure<AdminRecoveryResult>(Error.Validation(
                    "Recovery.UserDisabled",
                    $"'{target.Email}' was disabled at {stillDisabledAt:u}, moments ago — after this command "
                    + "started but before the reset committed. Resetting the password would NOT restore access. "
                    + "Re-enable them from the Users screen first, then re-run this command if the password is "
                    + "still unknown."));

            var password = TemporaryPassword.Generate();
            var reset = await identity.BreakGlassResetAsync(target.AccountId, target.Id, password, reason, token);
            if (reset.IsFailure)
                return Result.Failure<AdminRecoveryResult>(reset.Error);

            await transaction.CommitAsync(token);
            return Result.Success(new AdminRecoveryResult(target.Email!, target.AccountId, lockedAccount.Slug, password));
        }, ct);
    }
}

// #589 — Slug is a plain NON-NULLABLE string, unlike FirstRunAdminOutcome's
// nullable one. That record has a no-op path (AlreadyProvisioned returns a
// value with nothing populated) so every field must be nullable there;
// AdminRecoveryResult has no no-op path — recovery always ran — so every field
// is populated and none are nullable. The slug is read off `lockedAccount`
// (already loaded FOR UPDATE in the transaction, no new query) and printed by
// recover-admin because #532 made the farm code a required login input.
public sealed record AdminRecoveryResult(string Email, Guid AccountId, string Slug, string TemporaryPassword);
