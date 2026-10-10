namespace Cluckwork.Application.Tests.TenantBypass;

// #536 Part 1: the allow-list. One reviewable row per excused bypass (design M5/M7). The exemption lives
// apart from the code it excuses, so a bypass and its exemption are never the same keystroke.
//
// Rules:
//  * Symbol names the enclosing method in symbol display form (Namespace.Type.Method(paramTypes)). A call
//    inside a local function keys as ContainingMethod.Local(localFunctionName) and is NOT covered by the
//    parent's row.
//  * Hash is GuardScanner.TokenHash over that whole member, or over the function's name and the whole method
//    around a local function, whose locals it can capture: 8 hex characters of SHA-256 over its Roslyn tokens,
//    the #632 filter-free-set hasher. Comments and whitespace drop out; every other token, literals included, counts. Editing the
//    member un-excuses its bypasses until a reviewer re-reads it and pastes the new Hash the failure prints
//    (#1072). No two rows may share a Hash.
//  * A row matching zero sites is STALE and fails the build, so a deleted bypass cannot leave a live
//    exemption behind.
//  * Justification is mandatory. An unexplained exemption is the thing the guard exists to prevent.
public sealed class AllowListEntry
{
    public required string Symbol { get; init; }

    public required string File { get; init; }

    public required string Hash { get; init; }

    public required string Justification { get; init; }
}

// The one tenant-bypass allow-list every real-tree test reads (#859). Nothing else holds the real rows.
internal static class BypassAllowList
{
    private static readonly AllowListEntry[] Rows =
    [
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.AccountProvisioner.ProvisionAsync(string? name, string? slug, string? ownerEmail, string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountProvisioner.cs",
            Hash = "e99bb5a2",
            Justification = "Provisions a NEW account; checks the global slug for a collision before any tenant exists. Runs at unresolved tenant by design (#533).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.CredentialEpochVerifier.VerifyAsync(Guid userId, Guid accountId, int tokenEpoch, bool connectedApp, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/CredentialEpochVerifier.cs",
            Hash = "f7239ce8",
            Justification = "DEFENSIVE bypass: reads the user's own account to check IsActive and, for an OAuth token, AllowConnectedApps (#1146). The read is scoped to the JWT's account id; IgnoreQueryFilters makes it work even before TenantContext resolves. #364 fail-closed guarantee. Moved here from CredentialEpochMiddleware.InvokeAsync by #857, query unchanged.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.FirstRunAdminService.ProvisionAsync(string? email, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/FirstRunAdminService.cs",
            Hash = "dd08f719",
            Justification = "First-run admin bootstraps the DEFAULT account; checks for an existing Owner at unresolved tenant by design (#283).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.FirstRunAdminService.HoldsProvisioningLockAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/FirstRunAdminService.cs",
            Hash = "673eed0b",
            Justification = "Raw SQL advisory lock for first-run; not tenant-scoped (it is a process-level boot guard, #283).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.IdentityProvider.RefreshAsync(string refreshToken, CancellationToken ct, Guid? expectedAccountId)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/IdentityProvider.cs",
            Hash = "8ac33f82",
            Justification = "Refresh-token rotation reads the account to verify it is active; scoped to the token's account id, IgnoreQueryFilters because TenantContext may be unresolved during the auth handshake.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.IdentityProvider.ExecuteLineageFenceAsync(string currentHash, string[] ancestorHashes, Guid rootUserId, Guid rootAccountId, int rootIssuedEpoch, DateTimeOffset now, string rotatedStamp, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/IdentityProvider.cs",
            Hash = "aec72489",
            Justification = "Low-level raw-SQL data-modifying CTE for logout lineage fencing. Both refresh-token UPDATE arms are explicitly constrained by UserId, AccountId, and IssuedEpoch; the dedicated atomic-logout CTE guard pins both arms and the builder-to-RelationalCommand execution seam.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggOperationsFixture.ListSaleableGradeNamesAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggOperationsFixture.cs",
            Hash = "2caae988",
            Justification = "Non-Production simulation fixture port (#858); the seeder calls it at unresolved tenant by design (#279), scoped to the seeded account via explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.FarmFixture.AccountExistsAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/FarmFixture.cs",
            Hash = "12f8f221",
            Justification = "Non-Production simulation fixture port (#858); checks one farm's existence by id before the tenant is resolved, and the second, pristine farm's existence while the seeder's tenant is the primary farm (#279).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.FarmFixture.CountAccountsAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/FarmFixture.cs",
            Hash = "1b637014",
            Justification = "Non-Production simulation fixture port (#858); the manifest certifies exactly two farms on the deployment, so the count spans every farm by design (#279).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.GetCurrentSharedLockedAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "ac2376cc",
            Justification = "FOR SHARE lock on the current tenant's account row; the raw SQL carries WHERE \"Id\" = {tenant.AccountId} (the lock must be inside the raw SQL or it would lock every tenant's row, #162).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.GetCurrentLockedAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "7ca724a5",
            Justification = "FOR UPDATE lock on the current tenant's account row; the raw SQL carries WHERE \"Id\" = {tenant.AccountId} (#162).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.FindBySlugAsync(string slug, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "bb7e30c6",
            Justification = "Resolves an account by its global slug (slugs are unique across farms) for farm-code login, before any tenant is resolved. Only login may call it (FindBySlugCallerTests, #1053).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.ListTimeZonesAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "87a3545f",
            Justification = "IFarmDirectory (#858): lists every farm's id and time zone for the daily-entry lock sweep, which runs with no tenant resolved under the single-leader gate (#271) and then resolves one tenant scope per farm. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.ListAsync(CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "9ddba700",
            Justification = "IFarmDirectory (#858): list-accounts prints every farm's code, name and active state for the operator; no tenant is resolved. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository.FindIdBySlugAsync(string slug, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Farm/Repositories/AccountRepository.cs",
            Hash = "878bff76",
            Justification = "IFarmDirectory (#858): the operator verbs resolve a farm by its global code (slugs are unique across farms) before any tenant is resolved. Only the operator verbs and jobs may call the directory (FarmDirectoryCallerTests).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Insights.Repositories.AuditEventRepository.GetProvenanceChunkAsync(string entityType, Guid[] ids, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Insights/Repositories/AuditEventRepository.cs",
            Hash = "939dd727",
            Justification = "Audit provenance selects only AuditEvents. Each of the created, latest and promoted SQL statements scopes AccountId, EntityType and EntityId before any self-join; IgnoreQueryFilters prevents EF from composing its global filter onto those tenant-scoped raw queries.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.DailyEntryRepository.GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/DailyEntryRepository.cs",
            Hash = "0c8e7a59",
            Justification = "#388 write-side authorization lookup: bypasses the combined tenant+flock query filter so an OWN-account unassigned draft reaches FlockScopeGuard (preserving the 422 contract), then reinstates tenant isolation explicitly with e.AccountId == accountId so a foreign-account id still reads as null. READ endpoints never call this method and stay symmetric 404.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.FlockManagement.Repositories.FlockRepository.GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/FlockManagement/Repositories/FlockRepository.cs",
            Hash = "cfe394af",
            Justification = "#388 post-authorization lifecycle lookup: after live FlockScopeGuard succeeds, bypasses the request-start flock snapshot so a newly assigned flock is lifecycle-checked; explicitly reinstates AccountId so foreign ids remain null. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.FlockManagement.Repositories.FlockRepository.GetReadOnlyForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/FlockManagement/Repositories/FlockRepository.cs",
            Hash = "34fd3754",
            Justification = "#1022 untracked twin of GetByIdForFlockScopedWriteAsync, same query plus AsNoTracking, for IFlockLookup's snapshot reads: after live FlockScopeGuard succeeds, bypasses the request-start flock snapshot and explicitly reinstates AccountId so foreign ids remain null. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.DailyEntryRepository.FindByNaturalKeyForFlockScopedWriteAsync(Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/DailyEntryRepository.cs",
            Hash = "64d57feb",
            Justification = "#388 post-live-guard write/provenance natural-key lookup: bypasses the request-start flock snapshot so RecordDailyEntry, RecordFeedUsage and RecordWaterUsage see a newly assigned sibling's live entry state; reinstates AccountId explicitly and keeps the full natural key plus non-Voided predicate. Read endpoints never call it.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggLotRepository.GetAvailableFifoLockedAsync(Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs",
            Hash = "7e62c706",
            Justification = "FIFO sale-allocation lock; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement so only this tenant's lots are locked (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggLotRepository.GetByIdsLockedAsync(Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs",
            Hash = "6ba7c407",
            Justification = "Void-restore lock on specific lots; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement (#60, #313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggLotRepository.GetByDailyEntryLockedAsync(Guid accountId, Guid dailyEntryId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggLotRepository.cs",
            Hash = "12c16148",
            Justification = "Locks the lots generated by a daily entry; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.InventoryItemRepository.GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/GeneralInventory/Repositories/InventoryItemRepository.cs",
            Hash = "03d9c68f",
            Justification = "FOR UPDATE on an inventory item; the raw SQL carries WHERE \"Id\" = {id} AND \"AccountId\" = {accountId} so a foreign-tenant id matches no row and the lock is never attempted against it (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.InventoryLotRepository.GetAvailableFifoLockedAsync(Guid accountId, Guid inventoryItemId, DateOnly asOfDate, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/GeneralInventory/Repositories/InventoryLotRepository.cs",
            Hash = "213d24c4",
            Justification = "FIFO inventory-allocation lock; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.InventoryLotRepository.GetByIdLockedAsync(Guid accountId, Guid lotId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/GeneralInventory/Repositories/InventoryLotRepository.cs",
            Hash = "53bda742",
            Justification = "FOR UPDATE on an inventory lot; the raw SQL carries WHERE \"AccountId\" = {accountId} and the lock is inside the statement.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Commerce.Repositories.SalesOrderRepository.GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Commerce/Repositories/SalesOrderRepository.cs",
            Hash = "5ffbb895",
            Justification = "FOR UPDATE on a sales order; the raw SQL carries WHERE \"Id\" = {id} AND \"AccountId\" = {accountId} so a foreign-tenant id matches no row (#313).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Commerce.Repositories.CommerceFixture.PurgeOrdersAndCustomersAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Commerce/Repositories/CommerceFixture.cs",
            Hash = "f1dac703",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's order lines, orders and customers inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggOperationsFixture.AnyGradeAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggOperationsFixture.cs",
            Hash = "c1dc6262",
            Justification = "Non-Production demo fixture port (#858); the demo seeder's base-data check runs at unresolved tenant by design (#280), scoped by explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.EggOperations.Repositories.EggOperationsFixture.PurgeDailyEntriesAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/EggOperations/Repositories/EggOperationsFixture.cs",
            Hash = "e9b0e8f1",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's egg movements, lots, entry grades and entries inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.FlockManagement.Repositories.FlockFixture.AnyFlockAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/FlockManagement/Repositories/FlockFixture.cs",
            Hash = "5babdd1e",
            Justification = "Non-Production demo fixture port (#858); the demo seeder's already-seeded check runs at unresolved tenant by design (#280), scoped by explicit AccountId.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.FlockManagement.Repositories.FlockFixture.PurgeBirdMovementsAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/FlockManagement/Repositories/FlockFixture.cs",
            Hash = "3b782865",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's bird movements inside the seeder's transaction, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.FlockManagement.Repositories.FlockFixture.PurgeFlocksAsync(Guid accountId, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/FlockManagement/Repositories/FlockFixture.cs",
            Hash = "6523050a",
            Justification = "Non-Production demo fixture port (#858); demo partial-seed cleanup deletes the farm's flocks inside the seeder's transaction, after Egg Operations' purge, scoped by explicit AccountId (#280).",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.InvokeAsync(HttpContext context, AppDbContext db, TenantContext tenant, CurrentUserContext user)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Hash = "4c659505",
            Justification = "Raw-SQL UPDATE of idempotency_records on successful completion; the statement carries WHERE \"AccountId\" = {accountId} (the claim was scoped to this tenant on insert). Not a row lock, so the predicate walk does not flag it — allow-listed as a raw-SQL occurrence.",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.TryClaimOrInspectAsync(AppDbContext db, Guid accountId, string endpointHash, string keyHash, string requestHash, Guid ownerToken, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Hash = "be5dac99",
            Justification = "Raw-SQL UPDATE that steals an expired idempotency lease; the statement carries WHERE \"AccountId\" = {accountId} AND (EndpointHash, IdempotencyKeyHash) — scoped to this tenant's claim. Not a row lock.",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Middleware.IdempotencyMiddleware.ReleaseClaimAsync(AppDbContext db, Guid accountId, string endpointHash, string keyHash, Guid ownerToken, CancellationToken ct)",
            File = "src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs",
            Hash = "4a43d269",
            Justification = "Raw-SQL DELETE of the tenant's own idempotency claim; the statement carries WHERE \"AccountId\" = {accountId} AND (EndpointHash, IdempotencyKeyHash, LeaseOwner) — scoped to this tenant. Not a row lock.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.FirstRunAdminService.ProvisionUnderLockAsync(Guid accountId, string accountSlug, string email, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/FirstRunAdminService.cs",
            Hash = "28ffc7ff",
            Justification = "Caller of HoldsProvisioningLockAsync (a forwarding wrapper). First-run admin bootstrap runs at unresolved tenant by design (#283); the provisioning lock is a process-level advisory lock, not tenant-scoped.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Insights.Repositories.AuditEventRepository.GetProvenanceAsync(string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Insights/Repositories/AuditEventRepository.cs",
            Hash = "f0aa4b72",
            Justification = "Caller of GetProvenanceChunkAsync. Each wrapped AuditEvents query applies AccountId, EntityType and EntityId before its self-joins; the forwarding method chunks entity ids without changing that scope.",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.AccountRenameService.RenameAsync(string currentSlug, string? newSlug, string? reason, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountRenameService.cs",
            Hash = "b43347b9",
            Justification = "Caller of ResolveSlugsAsync (a forwarding wrapper). Operator CLI resolves the source farm by its globally unique code AND checks the destination code's availability in that one read, before any tenant is resolved. The service's locked read is tenant-keyed; the global IX_Accounts_Slug index and its unique-violation catch remain authoritative for the destination (#732). Same justified call site as AccountSlugLookup.ResolveAsync (#536).",
        },
        new()
        {
            Symbol = "Cluckwork.Infrastructure.Modules.Access.Identity.AccountRenameService.ResolveSlugsAsync(string currentSlug, string target, CancellationToken ct)",
            File = "src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountRenameService.cs",
            Hash = "3b4358c4",
            Justification = "One unresolved-tenant read covering both the source code and the destination code: the operator CLI runs before any tenant exists, so IgnoreQueryFilters is required rather than defensive. Neither half is an authority — the post-lock fence covers the source by comparing the locked row's slug AND Version against this read's snapshot, and the global IX_Accounts_Slug index plus its unique-violation catch cover the destination (#732).",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Hosting.PlaintextDataProtectionKeyGuard.StartingAsync(CancellationToken cancellationToken)",
            File = "src/Cluckwork.Api/Hosting/PlaintextDataProtectionKeyGuard.cs",
            Hash = "28cd5e33",
            Justification = "Caller of EnsureNoPlaintextDataProtectionKeysAsync (a forwarding call). The Production serving guard runs at host start, before any request, so no tenant exists; the key ring it checks belongs to the deployment, not to a farm (#794).",
        },
        new()
        {
            Symbol = "Cluckwork.Api.Hosting.PlaintextDataProtectionKeyGuard.EnsureNoPlaintextDataProtectionKeysAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)",
            File = "src/Cluckwork.Api/Hosting/PlaintextDataProtectionKeyGuard.cs",
            Hash = "ef589358",
            Justification = "Raw SQL to_regclass probe for the DataProtectionKeys table, which has no AccountId: one key ring serves every farm. The probe reads catalog metadata only, so a boot with pending migrations skips the check and /health/ready reports that state (#794, #263).",
        },
    ];

    // A row with a blank field excuses nothing: it is dropped, so its site is reported unexcused.
    internal static IReadOnlyList<AllowListEntry> Entries { get; } = Rows
        .Where(e => !string.IsNullOrWhiteSpace(e.Symbol)
            && !string.IsNullOrWhiteSpace(e.File)
            && !string.IsNullOrWhiteSpace(e.Hash)
            && !string.IsNullOrWhiteSpace(e.Justification))
        .ToList();
}
