namespace Cluckwork.Application.Tests.TenantBypass;

// One classified filter-free-set site (#632). The key joins the identity fields exactly as written; Reason is
// stored as written and trimmed where it is read.
internal sealed record FilterFreeSetSite(string Symbol, string Set, string Signature, string Reason)
{
    internal string Key => string.Join("\t", Symbol, Set, Signature);
}

// The one filter-free-set classification list every real-tree test reads (#859).
internal static class FilterFreeSetSites
{
    // symbol <TAB> db.<Set> <TAB> signature <TAB> reason. A row with fewer than four fields reads with blank
    // fields, so the real-tree test reports it as malformed.
    internal static IReadOnlyList<FilterFreeSetSite> All { get; } =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TenantBypass", "Data", "filter-free-set-sites.tsv"))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
            .Select(l => l.Split('\t'))
            .Select(p => new FilterFreeSetSite(Field(p, 0), Field(p, 1), Field(p, 2), Field(p, 3)))
            .ToList();

    // #859 parity phase: Data/filter-free-set-sites.tsv as C#. TenantRegistryParityTests holds the two equal.
    internal static readonly FilterFreeSetSite[] Rows =
    [
        new("Cluckwork.Api.Middleware.IdempotencyMiddleware.TryClaimOrInspectAsync(AppDbContext db, Guid accountId, string endpointHash, string keyHash, string requestHash, Guid ownerToken, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)",
            "db.IdempotencyRecords", "5a00a5f9",
            "by-key: unique index (AccountId, EndpointHash, IdempotencyKeyHash) is enforced by the DB; the method carries accountId and the record is written with it. The claim read is by (endpointHash, keyHash) within the caller's resolved account."),
        new("Cluckwork.Infrastructure.Identity.AccessSeedLookup.OwnerRoleExistsAsync(CancellationToken ct)",
            "db.Roles", "36c0e024",
            "global-reference: non-Production simulation fixture port (#858) checks the migration-baked Owner role (AspNetRoles is farm-wide reference data); the seeder calls it at unresolved tenant by design (#279)."),
        new("Cluckwork.Infrastructure.Identity.AccountScopedUserValidator.ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)",
            "db.Users", "104f7fd4",
            "by-id: re-reads the current user by primary key (candidate.Id == user.Id) to compare against the persisted row; the user's account is already resolved by the caller. #529 legacy-import guard."),
        new("Cluckwork.Infrastructure.Identity.AccountUserDirectory.FindByAccountRoleAsync(Guid accountId, string roleName, CancellationToken ct)",
            "db.Roles", "f4f82571",
            "global-reference: AspNetRoles has no AccountId column — roles are farm-wide reference data (the four base roles are global); the query is scoped by the join to the tenant's UserRows, not by an account predicate."),
        new("Cluckwork.Infrastructure.Identity.AccountUserDirectory.FindByAccountRoleAsync(Guid accountId, string roleName, CancellationToken ct)",
            "db.UserRoles", "f4f82571",
            "scoped-by-join: the role query joins db.Users (which carries AccountId) on user.Id, and the WHERE names user.AccountId == accountId; the UserRoles join is therefore tenant-scoped transitively."),
        new("Cluckwork.Infrastructure.Identity.AdminRecoveryService.RecoverAsync(string? email, Guid? accountId, string? reason, CancellationToken ct)",
            "db.Users", "59037298",
            "by-id+account: break-glass recovery looks up the user by email within the recovered account; the method takes accountId and the lookup is scoped to it. #265."),
        new("Cluckwork.Infrastructure.Identity.FirstRunStatusService.IsProvisionedAsync(CancellationToken ct)",
            "db.Roles", "fd78330f",
            "global-reference: role lookup by name (AspNetRoles is farm-wide reference data); scoped by the join to the tenant's user rows."),
        new("Cluckwork.Infrastructure.Identity.FirstRunStatusService.IsProvisionedAsync(CancellationToken ct)",
            "db.UserRoles", "fd78330f",
            "scoped-by-join: checks the Owner role by joining db.Users (carries AccountId) on user.Id; tenant-scoped transitively through the user row."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.ChangeOwnPasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)",
            "db.RefreshTokens", "fdec8b5b",
            "by-user: password change revokes the user's tokens by user id (account-unique)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.CountOtherActiveOwnersAsync(Guid accountId, Guid excludingUserId, CancellationToken token)",
            "db.Roles", "57009ecd",
            "global-reference: Owner-role filter by name in the owner-count (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.CountOtherActiveOwnersAsync(Guid accountId, Guid excludingUserId, CancellationToken token)",
            "db.UserRoles", "57009ecd",
            "scoped-by-user-id: counts other active Owners by joining UserRoles on the user id (account-unique) and the user's AccountId == accountId."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.DisableUserAsync(Guid accountId, Guid userId, Guid actingUserId, string? reason, CancellationToken ct)",
            "db.Roles", "9b6fec98",
            "global-reference: role lookup by name in the disable path (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.DisableUserAsync(Guid accountId, Guid userId, Guid actingUserId, string? reason, CancellationToken ct)",
            "db.UserRoles", "9b6fec98",
            "scoped-by-user-id: DisableUser READS the target's Owner membership by userRole.UserId == userId (account-unique) for the last-Owner check; it writes no role row (it disables the user, rotates the stamp, bumps the epoch, revokes refresh tokens)."),
        new("Cluckwork.Infrastructure.Identity.AccessLookup.GetEffectiveRoleAsync(Guid accountId, Guid userId, CancellationToken ct)",
            "db.Roles", "4adcdb84",
            "global-reference: role name resolution in the effective-role routine (#612; IAccessLookup owns it since #857) (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.AccessLookup.GetEffectiveRoleAsync(Guid accountId, Guid userId, CancellationToken ct)",
            "db.UserRoles", "4adcdb84",
            "scoped-by-account: the effective-role routine (#612; owned by IAccessLookup since #857) loads the user's roles; the method takes accountId and has already confirmed the user is an ACTIVE member of it via a preceding db.Users AnyAsync(u.Id == userId && u.AccountId == accountId && u.DisabledAt == null) check."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.GetUserAsync(Guid accountId, Guid userId, CancellationToken ct)",
            "db.Roles", "3696c8d2",
            "global-reference: role name resolution in GetUser (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.GetUserAsync(Guid accountId, Guid userId, CancellationToken ct)",
            "db.UserRoles", "3696c8d2",
            "scoped-by-account: GetUser loads the user's roles; the method takes accountId and the user lookup is scoped to it."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.InspectGraceReplacementAsync(RefreshToken revoked, DateTimeOffset now, CancellationToken ct)",
            "db.RefreshTokens", "2c93fbd6",
            "by-root-lineage: attaches only the replacement entity projected by the fresh hash + UserId + AccountId parent/child inspection; Attach performs no query and the later CAS save retains the row's AccountId."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.ListUsersAsync(Guid accountId, CancellationToken ct)",
            "db.Roles", "2eb84bd1",
            "global-reference: role name resolution in ListUsers (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.ListUsersAsync(Guid accountId, CancellationToken ct)",
            "db.UserRoles", "2eb84bd1",
            "scoped-by-account: lists the account's users with their roles; the query carries u.AccountId == accountId (the UserRoles join is scoped by the user rows)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.LoginAsync(Guid accountId, string email, string password, CancellationToken ct)",
            "db.RefreshTokens", "9d5e17f7",
            "by-hash: login rotates the user's refresh tokens by user id (account-unique); the token hash embeds the account."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.MintedTokenIsDurableAsync(string mintedHash)",
            "db.RefreshTokens", "737b7a77",
            "by-hash: the post-save durability probe uses the hash of 256-bit random bytes minted by this single attempt and not yet exposed; presence proves this attempt's transaction committed."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RefreshAsync(string refreshToken, CancellationToken ct, Guid? expectedAccountId)",
            "db.RefreshTokens", "84591a87",
            "by-hash: refresh-token rotation reads by token hash; the hash is account-scoped by construction."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RefreshAsync(string refreshToken, CancellationToken ct, Guid? expectedAccountId)",
            "db.RefreshTokens", "9d5e17f7",
            "by-hash: same rotation path, second read by token hash."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RequireActiveOwnerAsync(Guid accountId, Guid actingUserId, CancellationToken token)",
            "db.Roles", "aed7b297",
            "global-reference: role lookup by name in the Owner check (AspNetRoles is farm-wide reference data)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RequireActiveOwnerAsync(Guid accountId, Guid actingUserId, CancellationToken token)",
            "db.UserRoles", "aed7b297",
            "scoped-by-user-id: checks the acting user's Owner role by userRole.UserId == actingUserId (the user id is account-unique); the Role join is to global reference data."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RevokeAllActiveForUserAsync(Guid userId, int? issuedEpoch, DateTimeOffset now, CancellationToken ct)",
            "db.RefreshTokens", "34d066f2",
            "by-user: revokes all active tokens for a user by user id (account-unique)."),
        new("Cluckwork.Infrastructure.Identity.IdentityProvider.RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct, Guid? expectedAccountId)",
            "db.RefreshTokens", "1a753d3f",
            "by-hash: logout attribution reads by the globally unique opaque credential hash, then immediately compares the projected AccountId when a farm was selected; the projection is part of that scope gate. Review P1-3 surfaced this candidate because the old Contains(\"AccountId\") check mistook a projection for a predicate."),
        new("Cluckwork.Infrastructure.Identity.PersistentStepUpGrantRegistry.IsRevokedByLogoutAsync(Guid userId, int grantEpoch, CancellationToken ct)",
            "db.Users", "5e42ce0b",
            "by-id: checks revocation by the user's StepUpLogoutEpoch (account-unique)."),
        new("Cluckwork.Infrastructure.Identity.PersistentStepUpGrantRegistry.RecordLogoutAsync(Guid userId, CancellationToken ct)",
            "db.Users", "8f838564",
            "by-id: records a logout by bumping the user's StepUpLogoutEpoch (account-unique)."),
        new("Cluckwork.Infrastructure.Identity.PersistentStepUpGrantRegistry.TryConsumeIfNotLoggedOutAsync(Guid userId, Guid jti, int grantEpoch, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct)",
            "db.Users", "5e42ce0b",
            "by-id: reads the user's StepUpLogoutEpoch by user id (account-unique) to check the grant is not revoked; #338 shared-state."),
        new("Cluckwork.Infrastructure.Identity.RefreshTokenPurge.DeleteExpiredBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)",
            "db.RefreshTokens", "7d79d25a",
            "non-tenant sweep: purges expired refresh tokens across all tenants (single-leader gate #271); scoped by expiry."),
        new("Cluckwork.Infrastructure.Identity.StepUpGrantService.ValidateAsync(Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct)",
            "db.Users", "ed4ecf61",
            "by-id: validates the grant's security stamp by user id (account-unique), then rejects when the persisted user's AccountId does not match the grant account; the read is no-tracking so a later account-locked mutation cannot identity-resolve this pre-lock row."),
        new("Cluckwork.Infrastructure.Jobs.DurableJobWorker.ProcessPendingJobsAsync(CancellationToken ct)",
            "db.DurableJobs", "b4b9ba61",
            "non-tenant table: DurableJob has no AccountId column by design (#271 at-most-one-leader lease, single-leader gate); the worker runs with no tenant resolved and polls by status/RunAfter."),
        new("Cluckwork.Infrastructure.Jobs.IdempotencyRecordPurgeSweep.RunAsync(CancellationToken ct)",
            "db.IdempotencyRecords", "6ebd4014",
            "non-tenant sweep: second purge query in the same sweep; scoped by expiry."),
        new("Cluckwork.Infrastructure.Jobs.IdempotencyRecordPurgeSweep.RunAsync(CancellationToken ct)",
            "db.IdempotencyRecords", "d5b16b0c",
            "non-tenant sweep: purges expired records across all tenants (runs under the single-leader gate #271 with no tenant resolved); scoped by expiry, not by account."),
    ];

    private static string Field(string[] parts, int index) => index < parts.Length ? parts[index] : string.Empty;
}
