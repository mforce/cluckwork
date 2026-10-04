namespace Cluckwork.Application.Tests.TenantBypass;

// #536 Part 1: the allow-list. One reviewable row per excused bypass (design M5/M7). The exemption lives
// apart from the code it excuses, so a bypass and its exemption are never the same keystroke.
//
// Rules:
//  * Symbol names the enclosing method in symbol display form (Namespace.Type.Method(paramTypes)). A call
//    inside a local function keys as ContainingMethod.Local(localFunctionName) and is NOT covered by the
//    parent's row.
//  * A row matching zero sites is STALE and fails the build, so a deleted bypass cannot leave a live
//    exemption behind.
//  * Justification is mandatory. An unexplained exemption is the thing the guard exists to prevent.
public sealed class AllowListEntry
{
    public required string Symbol { get; init; }

    public required string File { get; init; }

    public required string Justification { get; init; }
}

// The one tenant-bypass allow-list every real-tree test reads (#859). Nothing else holds the real rows.
internal static class BypassAllowList
{
    private static readonly AllowListEntry[] Rows =
    [
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.AccountProvisioner.ProvisionAsync(string? name, string? slug, string? ownerEmail, string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/AccountProvisioner.cs",
            Justification = "Provisions a NEW account; checks the global slug for a collision before any tenant exists. Runs at unresolved tenant by design (#533).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.CredentialEpochVerifier.VerifyAsync(Guid userId, Guid accountId, int tokenEpoch, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/CredentialEpochVerifier.cs",
            Justification = "DEFENSIVE bypass: reads the user's own account to check IsActive. The read is scoped to the JWT's account id; IgnoreQueryFilters makes it work even before TenantContext resolves. #364 fail-closed guarantee. Moved here from CredentialEpochMiddleware.InvokeAsync by #857, query unchanged.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.FirstRunAdminService.ProvisionAsync(string? email, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/FirstRunAdminService.cs",
            Justification = "First-run admin bootstraps the DEFAULT account; checks for an existing Owner at unresolved tenant by design (#283).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.FirstRunAdminService.HoldsProvisioningLockAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/FirstRunAdminService.cs",
            Justification = "Raw SQL advisory lock for first-run; not tenant-scoped (it is a process-level boot guard, #283).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.IdentityProvider.RefreshAsync(string refreshToken, CancellationToken ct, Guid? expectedAccountId)",
            File = "src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs",
            Justification = "Refresh-token rotation reads the account to verify it is active; scoped to the token's account id, IgnoreQueryFilters because TenantContext may be unresolved during the auth handshake.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.IdentityProvider.ExecuteLineageFenceAsync(string currentHash, string[] ancestorHashes, Guid rootUserId, Guid rootAccountId, int rootIssuedEpoch, DateTimeOffset now, string rotatedStamp, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs",
            Justification = "Low-level raw-SQL data-modifying CTE for logout lineage fencing. Both refresh-token UPDATE arms are explicitly constrained by UserId, AccountId, and IssuedEpoch; the dedicated atomic-logout CTE guard pins both arms and the builder-to-RelationalCommand execution seam.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggOperationsFixture.ListSaleableGradeNamesAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggOperationsFixture.cs",
            Justification = "Non-Production simulation fixture port (#858); the seeder calls it at unresolved tenant by design (#279), scoped to the seeded account via explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FarmFixture.AccountExistsAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FarmFixture.cs",
            Justification = "Non-Production simulation fixture port (#858); checks one farm's existence by id before the tenant is resolved, and the second, pristine farm's existence while the seeder's tenant is the primary farm (#279).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FarmFixture.CountAccountsAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FarmFixture.cs",
            Justification = "Non-Production simulation fixture port (#858); the manifest certifies exactly two farms on the deployment, so the count spans every farm by design (#279).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.GetCurrentSharedLockedAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "FOR SHARE lock on the current tenant's account row; the raw SQL carries WHERE \"Id\" = {tenant.AccountId} (the lock must be inside the raw SQL or it would lock every tenant's row, #162).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.GetCurrentLockedAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "FOR UPDATE lock on the current tenant's account row; the raw SQL carries WHERE \"Id\" = {tenant.AccountId} (#162).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.FindBySlugAsync(string slug, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "Resolves an account by its global slug (slugs are unique across farms) for farm-code login, before any tenant is resolved. Only login may call it (FindBySlugCallerTests, #1053).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.ListTimeZonesAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "IFarmDirectory (#858): lists every farm's id and time zone for the daily-entry lock sweep, which runs with no tenant resolved under the single-leader gate (#271) and then resolves one tenant scope per farm. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.ListAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "IFarmDirectory (#858): list-accounts prints every farm's code, name and active state for the operator; no tenant is resolved. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.AccountRepository.FindIdBySlugAsync(string slug, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
            Justification = "IFarmDirectory (#858): the operator verbs resolve a farm by its global code (slugs are unique across farms) before any tenant is resolved. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Insights.AuditEventRepository.GetProvenanceChunkAsync(string entityType, Guid[] ids, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Insights/AuditEventRepository.cs",
            Justification = "Audit provenance selects only AuditEvents. Each of the created, latest and promoted SQL statements scopes AccountId, EntityType and EntityId before any self-join; IgnoreQueryFilters prevents EF from composing its global filter onto those tenant-scoped raw queries.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.DailyEntryRepository.GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/DailyEntryRepository.cs",
            Justification = "#388 write-side authorization lookup: bypasses the combined tenant+flock query filter so an OWN-account unassigned draft reaches FlockScopeGuard (preserving the 422 contract), then reinstates tenant isolation explicitly with e.AccountId == accountId so a foreign-account id still reads as null. READ endpoints never call this method and stay symmetric 404.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FlockRepository.GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FlockRepository.cs",
            Justification = "#388 post-authorization lifecycle lookup: after live FlockScopeGuard succeeds, bypasses the request-start flock snapshot so a newly assigned flock is lifecycle-checked; explicitly reinstates AccountId so foreign ids remain null. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FlockRepository.GetReadOnlyForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FlockRepository.cs",
            Justification = "#1022 untracked twin of GetByIdForFlockScopedWriteAsync, same query plus AsNoTracking, for IFlockLookup's snapshot reads: after live FlockScopeGuard succeeds, bypasses the request-start flock snapshot and explicitly reinstates AccountId so foreign ids remain null. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.DailyEntryRepository.FindByNaturalKeyForFlockScopedWriteAsync(Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/DailyEntryRepository.cs",
            Justification = "#388 post-live-guard write/provenance natural-key lookup: bypasses the request-start flock snapshot so RecordDailyEntry, RecordFeedUsage and RecordWaterUsage see a newly assigned sibling's live entry state; reinstates AccountId explicitly and keeps the full natural key plus non-Voided predicate. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggLotRepository.GetAvailableFifoLockedAsync(Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs",
            Justification = "FIFO sale-allocation lock; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement so only this tenant's lots are locked (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggLotRepository.GetByIdsLockedAsync(Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs",
            Justification = "Void-restore lock on specific lots; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement (#60, #313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggLotRepository.GetByDailyEntryLockedAsync(Guid accountId, Guid dailyEntryId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs",
            Justification = "Locks the lots generated by a daily entry; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.InventoryItemRepository.GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/InventoryItemRepository.cs",
            Justification = "FOR UPDATE on an inventory item; the raw SQL carries WHERE \"Id\" = {id} AND \"AccountId\" = {accountId} so a foreign-tenant id matches no row and the lock is never attempted against it (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.InventoryLotRepository.GetAvailableFifoLockedAsync(Guid accountId, Guid inventoryItemId, DateOnly asOfDate, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/InventoryLotRepository.cs",
            Justification = "FIFO inventory-allocation lock; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.InventoryLotRepository.GetByIdLockedAsync(Guid accountId, Guid lotId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/InventoryLotRepository.cs",
            Justification = "FOR UPDATE on an inventory lot; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.SalesOrderRepository.GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/SalesOrderRepository.cs",
            Justification = "FOR UPDATE on a sales order; the raw SQL carries WHERE \"Id\" = {id} AND \"AccountId\" = {accountId} so a foreign-tenant id matches no row (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.CommerceFixture.PurgeOrdersAndCustomersAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/CommerceFixture.cs",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's order lines, orders and customers inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggOperationsFixture.AnyGradeAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggOperationsFixture.cs",
            Justification = "Non-Production demo fixture port (#858); the demo seeder's base-data check runs at unresolved tenant by design (#280), scoped by explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.EggOperationsFixture.PurgeDailyEntriesAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/EggOperationsFixture.cs",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's egg movements, lots, entry grades and entries inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FlockFixture.AnyFlockAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FlockFixture.cs",
            Justification = "Non-Production demo fixture port (#858); the demo seeder's already-seeded check runs at unresolved tenant by design (#280), scoped by explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FlockFixture.PurgeBirdMovementsAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FlockFixture.cs",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's bird movements inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Repositories.FlockFixture.PurgeFlocksAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Repositories/FlockFixture.cs",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's flocks inside the seeder's transaction, after Egg Operations' purge, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.InvokeAsync(HttpContext context, AppDbContext db, TenantContext tenant, CurrentUserContext user)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Justification = "Raw-SQL UPDATE of idempotency_records on successful completion; the statement carries WHERE \"AccountId\" = {accountId} (the claim was scoped to this tenant on insert). Not a row lock, so the predicate walk does not flag it — allow-listed as a raw-SQL occurrence.",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.TryClaimOrInspectAsync(AppDbContext db, Guid accountId, string endpointHash, string keyHash, string requestHash, Guid ownerToken, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Justification = "Raw-SQL UPDATE that steals an expired idempotency lease; the statement carries WHERE \"AccountId\" = {accountId} AND (EndpointHash, IdempotencyKeyHash) — scoped to this tenant's claim. Not a row lock.",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.ReleaseClaimAsync(AppDbContext db, Guid accountId, string endpointHash, string keyHash, Guid ownerToken, CancellationToken ct)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Justification = "Raw-SQL DELETE of the tenant's own idempotency claim; the statement carries WHERE \"AccountId\" = {accountId} AND (EndpointHash, IdempotencyKeyHash, LeaseOwner) — scoped to this tenant. Not a row lock.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.FirstRunAdminService.ProvisionUnderLockAsync(Guid accountId, string accountSlug, string email, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/FirstRunAdminService.cs",
            Justification = "Caller of HoldsProvisioningLockAsync (a forwarding wrapper). First-run admin bootstrap runs at unresolved tenant by design (#283); the provisioning lock is a process-level advisory lock, not tenant-scoped.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Insights.AuditEventRepository.GetProvenanceAsync(string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Insights/AuditEventRepository.cs",
            Justification = "Caller of GetProvenanceChunkAsync. Each wrapped AuditEvents query applies AccountId, EntityType and EntityId before its self-joins; the forwarding method chunks entity ids without changing that scope.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.AccountRenameService.RenameAsync(string currentSlug, string? newSlug, string? reason, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/AccountRenameService.cs",
            Justification = "Caller of ResolveSlugsAsync (a forwarding wrapper). Operator CLI resolves the source farm by its globally unique code AND checks the destination code's availability in that one read, before any tenant is resolved. The service's locked read is tenant-keyed; the global IX_Accounts_Slug index and its unique-violation catch remain authoritative for the destination (#732). Same justified call site as AccountSlugLookup.ResolveAsync (#536).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Identity.AccountRenameService.ResolveSlugsAsync(string currentSlug, string target, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Identity/AccountRenameService.cs",
            Justification = "One unresolved-tenant read covering both the source code and the destination code: the operator CLI runs before any tenant exists, so IgnoreQueryFilters is required rather than defensive. Neither half is an authority — the post-lock fence covers the source by comparing the locked row's slug AND Version against this read's snapshot, and the global IX_Accounts_Slug index plus its unique-violation catch cover the destination (#732).",
        },
    ];

    // A row with a blank field excuses nothing: it is dropped, so its site is reported unexcused.
    internal static IReadOnlyList<AllowListEntry> Entries { get; } = Rows
        .Where(e => !string.IsNullOrWhiteSpace(e.Symbol)
            && !string.IsNullOrWhiteSpace(e.File)
            && !string.IsNullOrWhiteSpace(e.Justification))
        .ToList();
}
