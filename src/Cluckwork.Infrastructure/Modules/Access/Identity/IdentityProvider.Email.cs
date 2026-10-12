using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    public Task<Result> ChangeUserEmailAsync(
        Guid accountId, Guid userId, string email, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            if (string.Equals(user.Email, email, StringComparison.Ordinal))
                return Result.Success();

            if (userId == actingUserId
                && await CountOtherActiveOwnersAsync(accountId, userId, token) == 0)
            {
                return Result.Failure(Error.Validation(
                    "Users.LastOwner",
                    "To change the last active Owner's email, add a second Owner first."));
            }

            var entry = db.Entry(user);
            var targetSnapshot = TrackedEntrySnapshot.Capture(entry);
            var priorAudits = db.ChangeTracker.Entries<Cluckwork.Domain.Auditing.AuditEvent>()
                .Select(auditEntry => auditEntry.Entity)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            // Both UserManager.UpdateSecurityStampAsync and the final save use
            // this shared scoped context. Suspend every caller-owned dirty
            // entry before either can flush it, then restore the exact pending
            // state after our save. Temporary generated keys are deliberately
            // never sent to PostgreSQL, so restoration never has to replace a
            // real generated key with its old temporary value.
            var priorDirtyEntries = db.ChangeTracker.Entries()
                .Where(candidate => !ReferenceEquals(candidate.Entity, user)
                    && candidate.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(TrackedEntrySnapshot.Capture)
                .ToArray();

            void RestoreTrackerAfterFailure()
            {
                targetSnapshot.RestoreTargetAfterFailure();
                foreach (var dirtyEntry in priorDirtyEntries)
                    dirtyEntry.RestoreExact();

                foreach (var auditEntry in db.ChangeTracker.Entries<Cluckwork.Domain.Auditing.AuditEvent>()
                             .Where(candidate => candidate.State == EntityState.Added
                                 && candidate.Entity.Action == AuditActions.UserEmailChanged
                                 && candidate.Entity.EntityId == userId
                                 && !priorAudits.Contains(candidate.Entity)))
                {
                    auditEntry.State = EntityState.Detached;
                }
            }

            void RestoreCallerEntriesAfterSuccess()
            {
                targetSnapshot.RestoreTargetAfterSuccessfulSave();
                foreach (var dirtyEntry in priorDirtyEntries)
                    dirtyEntry.RestoreExact();
            }

            try
            {
                // Suspension mutates tracker state, so it belongs inside the
                // same restore boundary as both saves. If a later snapshot
                // throws while being suppressed, the earlier ones are still
                // restored exactly by the catch below.
                targetSnapshot.Suspend(isTarget: true);
                foreach (var dirtyEntry in priorDirtyEntries)
                    dirtyEntry.Suspend(isTarget: false);

                var oldEmail = user.Email;
                user.Email = email;
                user.NormalizedEmail = userManager.NormalizeEmail(email);
                user.UserName = email;
                user.NormalizedUserName = userManager.NormalizeName(email);

                var stampRotated = await userManager.UpdateSecurityStampAsync(user);
                if (!stampRotated.Succeeded)
                {
                    RestoreTrackerAfterFailure();
                    if (IsConcurrencyFailure(stampRotated))
                        return Result.Failure(Error.Conflict(
                            "Users.Conflict", "The user was modified by another request. Reload and retry."));
                    if (stampRotated.Errors.Any(error =>
                            error.Code is "DuplicateEmail" or "DuplicateUserName"))
                    {
                        return Result.Failure(DuplicateEmailConflict());
                    }

                    return Result.Failure(Error.Validation(
                        "Users.EmailChangeFailed", Describe(stampRotated)));
                }

                user.CredentialEpoch++;
                await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), token);
                await audit.WriteAsync(AuditActions.UserEmailChanged, "User", user.Id,
                    reason: null, details: new { oldEmail, newEmail = email }, ct: token);

                try
                {
                    await db.SaveChangesAsync(token);
                }
                catch (DbUpdateConcurrencyException)
                {
                    RestoreTrackerAfterFailure();
                    return Result.Failure(Error.Conflict(
                        "Users.Conflict", "The user was modified by another request. Reload and retry."));
                }
                catch (DbUpdateException exception) when (IsUserEmailConflict(exception))
                {
                    RestoreTrackerAfterFailure();
                    return Result.Failure(DuplicateEmailConflict());
                }

                RestoreCallerEntriesAfterSuccess();
                await transaction.CommitAsync(token);
                return Result.Success();
            }
            catch (DbUpdateException exception) when (IsUserEmailConflict(exception))
            {
                RestoreTrackerAfterFailure();
                return Result.Failure(DuplicateEmailConflict());
            }
            catch
            {
                RestoreTrackerAfterFailure();
                throw;
            }
        }, ct);

    private static Error DuplicateEmailConflict() =>
        Error.Conflict("Users.DuplicateEmail", "A user with this email already exists.");

    internal static bool IsUserEmailConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "EmailIndex" or "UserNameIndex"
        };

    private sealed record TrackedEntrySnapshot(
        EntityEntry Entry,
        EntityState State,
        PropertyValues CurrentValues,
        PropertyValues OriginalValues,
        IReadOnlyDictionary<string, bool> ModifiedFlags,
        IReadOnlySet<string> TemporaryProperties,
        IReadOnlyDictionary<string, object?> TemporaryClrValues)
    {
        public static TrackedEntrySnapshot Capture(EntityEntry entry) => new(
            entry,
            entry.State,
            entry.CurrentValues.Clone(),
            entry.OriginalValues.Clone(),
            entry.Properties.ToDictionary(
                property => property.Metadata.Name,
                property => property.IsModified,
                StringComparer.Ordinal),
            entry.Properties
                .Where(property => property.IsTemporary)
                .Select(property => property.Metadata.Name)
                .ToHashSet(StringComparer.Ordinal),
            entry.Properties
                .Where(property => property.IsTemporary && property.Metadata.PropertyInfo is not null)
                .ToDictionary(
                    property => property.Metadata.Name,
                    property => property.Metadata.PropertyInfo!.GetValue(entry.Entity),
                    StringComparer.Ordinal));

        public void Suspend(bool isTarget)
        {
            foreach (var propertyName in TemporaryProperties)
                Entry.Property(propertyName).IsTemporary = false;
            Entry.State = EntityState.Unchanged;
            if (isTarget)
                return;
            // The state transition reverts Modified current values in EF Core
            // 10. Put the caller's values back before either save, then make
            // the suppression originals match so DetectChanges stays silent.
            // This happens before any store-generated key can be accepted;
            // restoration after the save never writes current values.
            foreach (var property in Entry.Properties
                         .Where(property => !TemporaryProperties.Contains(property.Metadata.Name)))
            {
                property.CurrentValue = CurrentValues[property.Metadata.Name];
            }
            Entry.OriginalValues.SetValues(CurrentValues);
            Entry.State = EntityState.Unchanged;
        }

        public void RestoreExact()
        {
            Entry.State = State;
            Entry.OriginalValues.SetValues(OriginalValues);
            foreach (var property in Entry.Properties)
                property.IsModified = ModifiedFlags[property.Metadata.Name];
            foreach (var propertyName in TemporaryProperties)
                Entry.Property(propertyName).IsTemporary = true;
            foreach (var (propertyName, clrValue) in TemporaryClrValues)
                Entry.Property(propertyName).Metadata.PropertyInfo!.SetValue(Entry.Entity, clrValue);
        }

        public void RestoreTargetAfterFailure()
        {
            Entry.CurrentValues.SetValues(CurrentValues);
            RestoreExact();
        }

        public void RestoreTargetAfterSuccessfulSave()
        {
            if (State == EntityState.Modified)
            {
                foreach (var property in Entry.Properties.Where(property => ModifiedFlags[property.Metadata.Name]))
                {
                    property.CurrentValue = CurrentValues[property.Metadata.Name];
                    property.OriginalValue = OriginalValues[property.Metadata.Name];
                    property.IsModified = true;
                }
                return;
            }

            // A queried target cannot be Added. Preserve a pre-existing delete
            // against the successfully updated row using the new accepted
            // concurrency originals rather than the stale pre-update ones.
            Entry.State = State;
        }
    }
}
