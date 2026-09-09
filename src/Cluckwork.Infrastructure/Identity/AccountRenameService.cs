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
//   1. The row mutated is still the farm the operator named when lookup ran. Resolving
//      slug -> id and taking FOR UPDATE are TWO awaited database statements, not one atomic
//      operation: another transaction can rename that row between them. The locked row's
//      slug is therefore compared with currentSlug before mutation. Without that fence a
//      stale command overwrites the rename that won the race.
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

        // This lookup and the locked read below are separate statements. The id is stable,
        // but the slug on that row is not; the post-lock equality fence below closes that
        // race. Resolving the tenant remains a precondition for the locked read.
        var accountId = await ResolveCurrentAsync(currentSlug, ct);
        if (accountId is null)
            return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", currentSlug));

        // Friendly UX only. This can race with another farm targeting the same code,
        // so IX_Accounts_Slug and the catch below remain the correctness guarantee.
        if (!string.Equals(currentSlug, target, StringComparison.Ordinal)
            && await IsTargetTakenAsync(target, accountId.Value, ct))
            return SlugTaken(target);

        tenant.Resolve(accountId.Value);

        // #500 — no signed-in human by design (an operator at a shell), so this declares
        // WHICH non-person it is, exactly as the suspend/reactivate/provision verbs do.
        currentUser.ResolveSystemActor(SystemActors.RenameAccount);

        try
        {
            return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
            {
                var account = await accounts.GetCurrentLockedAsync(token);
                if (account is null)
                    return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", accountId.Value));

                // The slug lookup happened before this lock. A concurrent committed rename
                // leaves the id valid but the operator's source code stale; never overwrite it.
                if (!string.Equals(account.Slug, currentSlug, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountRenameOutcome>(Error.Conflict(
                        "Account.SlugStale",
                        $"'{currentSlug}' is no longer this farm's code. Run list-accounts and "
                        + "re-run with the code it has now."));
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

    // A friendly early answer, never the authority: another transaction can still claim
    // target after this read. The unique index + catch above closes that race.
    private Task<bool> IsTargetTakenAsync(string target, Guid sourceId, CancellationToken ct) =>
        db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(account => account.Id != sourceId && account.Slug == target, ct);

    // Reads ACROSS accounts with no tenant resolved, so IgnoreQueryFilters is required
    // rather than defensive — without it the account filter matches Guid.Empty and every
    // real farm reads as absent. This is the same justified call site as
    // AccountSlugLookup.ResolveAsync, and #536's registry needs its own row.
    private async Task<Guid?> ResolveCurrentAsync(string slug, CancellationToken ct)
    {
        var matches = await db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(account => account.Slug == slug)
            .Select(account => account.Id)
            .ToListAsync(ct);
        // Slug carries a global unique index, so 0 or 1. Count==1 rather than
        // SingleOrDefault so a hand-corrupted database reads as "no such farm"
        // (the quieter failure for an operator tool) instead of throwing.
        return matches.Count == 1 ? matches[0] : null;
    }
}

// Changed = "this command changed the code", so the verb can tell an operator their
// re-run was a no-op without re-reading the database. Deliberately NOT "the farm is fine".
public sealed record AccountRenameOutcome(bool Changed);
