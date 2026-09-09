namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #732 — changes a farm's code. The operator surface is the `rename-account` verb; this is
// the domain path that replaces the guarded raw UPDATE #731 documented, which bumped
// Version by hand, wrote no audit row and checked neither the pattern nor the reserved set.
//
// Three things have to be true at once, which is why this is a service and not a handler:
//
//   1. The row mutated is still the row the operator named when lookup ran. Resolving
//      slug -> id and taking FOR UPDATE are TWO awaited database statements, not one
//      atomic operation: another transaction can write that row between them. The lookup
//      therefore snapshots Slug AND Version, and the locked row must still match BOTH.
//      Slug alone is not an identity: old -> other -> old commits two renames and leaves
//      the code the lookup read, so a slug-only fence passes and overwrites both. Version
//      is the aggregate's own change counter, so it separates "the row I looked at" from
//      "a row that merely looks the same". Without that fence a stale command overwrites
//      the rename that won the race.
//   2. The destination code stays unique. IX_Accounts_Slug is UNIQUE ("Slug") with no
//      account component, so the source row's lock reserves nothing: two farms renamed to
//      one code both pass any pre-read and the INDEX decides. The friendly pre-read below
//      is convenience; the catch is the guarantee (same split as AccountProvisioner).
//   3. The rename and its audit row commit together or not at all. IAuditWriter appends to
//      this unit of work and never saves, so the row lands with the rename or not at all.
//
// Deliberately NOT here: a retired-code list. A code a farm has moved off is immediately
// reusable, and `rename-account --slug <retired>` therefore targets whoever holds it now.
// That is the accepted cost of #732 and docs/decisions/732-farm-code-rename.md says so;
// the verb's help text and the runbook both tell the operator to run list-accounts first.
public sealed class AccountRenameService(
    AppDbContext db,
    TenantContext tenant,
    IAccountRepository accounts,
    IAuditWriter audit,
    CurrentUserContext currentUser)
{
    public async Task<Result<AccountRenameOutcome>> RenameAsync(
        string currentSlug, string? newSlug, string? reason, CancellationToken ct = default)
    {
        var validated = Account.TryValidateSlug(newSlug);
        if (validated.IsFailure)
            return Result.Failure<AccountRenameOutcome>(validated.Error);
        var target = validated.Value;

        // ONE friendly combined read for both codes. It answers "which farm holds the
        // source code?" and "is the destination already someone else's?" in a single
        // unresolved-tenant query — #732 review round 1 (F7): two separate reads meant
        // two forwarding occurrences under one allow-list key, and that key cannot
        // excuse them separately. This read and the locked read below are still separate
        // statements: the id is stable, but the slug on that row is not, and the
        // post-lock equality fence closes that race. The destination half is UX only —
        // it can race with another farm targeting the same code, so IX_Accounts_Slug and
        // the catch below remain the correctness guarantee.
        var resolution = await ResolveSlugsAsync(currentSlug, target, ct);
        if (resolution.CurrentId is null)
            return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", currentSlug));
        if (resolution.TargetTaken)
            return SlugTaken(target);

        var accountId = resolution.CurrentId.Value;
        var snapshotVersion = resolution.CurrentVersion;

        // Resolving the tenant is a precondition for the locked read, not a formality.
        tenant.Resolve(accountId);

        // #500 — no signed-in human by design (an operator at a shell), so this declares
        // WHICH non-person it is, exactly as the suspend/reactivate/provision verbs do.
        currentUser.ResolveSystemActor(SystemActors.RenameAccount);

        try
        {
            return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
            {
                var account = await accounts.GetCurrentLockedAsync(token);
                if (account is null)
                    return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", accountId));

                // The lookup happened before this lock, and BOTH of the facts it captured
                // can go stale in between. A committed rename leaves the id valid and the
                // operator's source code wrong. A rename that went away and came back
                // (old -> other -> old) leaves the code identical and the row two writes
                // further on, which the code alone cannot detect — hence Version, the
                // aggregate's own change counter, as the second half of the fence.
                //
                // Version also advances for writes that are not renames (a Farm Settings
                // save, a suspend), so this refuses a rename that raced one of those even
                // though the code is untouched. That is the deliberate direction: the
                // operator re-runs after list-accounts, and no schedule silently overwrites
                // a committed write.
                //
                // Neither half subsumes the other. Version catches a domain write that
                // restored the code; the slug comparison catches a write that changed the
                // code WITHOUT advancing Version, which is precisely what a raw UPDATE
                // outside the domain does — #731's retired procedure being the example.
                if (!string.Equals(account.Slug, currentSlug, StringComparison.Ordinal)
                    || account.Version != snapshotVersion)
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountRenameOutcome>(Error.Conflict(
                        "Account.SlugStale",
                        $"This farm changed after this command read '{currentSlug}', so the "
                        + "command is stale and nothing was written. Run list-accounts and "
                        + "re-run against the code it has now."));
                }

                // Read BEFORE mutating: Rename sets Slug, so asking the aggregate
                // afterwards cannot answer "did this command change anything?" — the same
                // pre-mutation read AccountSuspensionService makes of IsActive.
                var changed = !string.Equals(account.Slug, target, StringComparison.Ordinal);

                var rename = account.Rename(target);
                if (rename.IsFailure)
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountRenameOutcome>(rename.Error);
                }

                // Written only on a real change, so the trail is one row per change rather
                // than one per keystroke. The action is a direct AuditActions reference
                // because AuditVocabularyCoverageTests fails closed on any other shape.
                if (changed)
                    await audit.WriteAsync(
                        AuditActions.AccountRename, nameof(Account), account.Id,
                        reason: reason,
                        // from/to because the code the row names no longer exists, and the
                        // same accountability payload break-glass and suspension carry: the
                        // actor names the COMMAND, so the shell it ran on is the only trace.
                        details: new
                        {
                            from = currentSlug,
                            to = target,
                            host = Environment.MachineName,
                            osUser = Environment.UserName,
                        },
                        ct: token);

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return Result.Success(new AccountRenameOutcome(changed));
            }, ct);
        }
        catch (DbUpdateException ex) when (AccountProvisioner.IsSlugConflict(ex))
        {
            return SlugTaken(target);
        }
    }

    private static Result<AccountRenameOutcome> SlugTaken(string target) =>
        Result.Failure<AccountRenameOutcome>(Error.Conflict(
            "Account.SlugTaken",
            $"'{target}' is already another farm's code. Choose another; codes are unique "
            + "across every farm on this deployment."));

    // ONE unresolved-tenant read for BOTH codes (#732 review round 1, F7). Reads ACROSS
    // accounts with no tenant resolved, so IgnoreQueryFilters is required rather than
    // defensive — without it the account filter matches Guid.Empty and every real farm
    // reads as absent. Same justified call site as AccountSlugLookup.ResolveAsync, and
    // #536's registry needs its own row.
    //
    // Neither half is an authority. The source row's id is stable but everything else
    // about it can move, which is what the post-lock fence in RenameAsync is for — so
    // Version is projected beside Slug and the fence compares both; the destination
    // answer is friendly UX that another transaction can invalidate the moment it is
    // read, which is what IX_Accounts_Slug and the unique-violation catch are for.
    private async Task<SlugResolution> ResolveSlugsAsync(
        string currentSlug, string target, CancellationToken ct)
    {
        var matches = await db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(account => account.Slug == currentSlug || account.Slug == target)
            .Select(account => new { account.Id, account.Slug, account.Version })
            .ToListAsync(ct);
        // Slug carries a global unique index, so 0 or 1 rows hold the source code.
        // Count==1 rather than SingleOrDefault so a hand-corrupted database reads as
        // "no such farm" (the quieter failure for an operator tool) instead of throwing.
        var sources = matches.Where(account => account.Slug == currentSlug).ToList();
        var source = sources.Count == 1 ? sources[0] : null;
        var targetTaken = source is not null
            && !string.Equals(currentSlug, target, StringComparison.Ordinal)
            && matches.Any(account => account.Slug == target && account.Id != source.Id);
        return new SlugResolution(source?.Id, source?.Version ?? 0, targetTaken);
    }

    // CurrentVersion is the source row's Version as the lookup saw it, and is meaningful
    // only when CurrentId is set — RenameAsync returns NotFound one statement earlier
    // otherwise.
    private sealed record SlugResolution(Guid? CurrentId, int CurrentVersion, bool TargetTaken);
}

// Changed = "this command changed the code", so the verb can tell an operator their
// re-run was a no-op without re-reading the database. Deliberately NOT "the farm is fine".
public sealed record AccountRenameOutcome(bool Changed);
