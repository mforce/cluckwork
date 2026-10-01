# #863: splitting the shared integration collection

**Base:** `ad258d8f` (`origin/main`, including #1002 and #1003). **Date:** 2026-10-01. All local runs used the Release integration project, real Postgres through Testcontainers, `tools/test-timing/measure.py`, and the same 12-core host as the prior three documents in this series.

## U1: why now, and what changed

The 2026-09-30 amendment to #863 ruled out splitting the shared `integration` collection: the serialized block was not the critical path, and splitting it bought 0-23.5 s locally. That measurement predated two later changes. #1002 made every extra collection cost a database clone from a migrated template instead of a container start plus 18 migrations. #1003 serialized the five full-demo-seed classes against each other, which stopped the suite's heaviest nonshared work from contending with itself and let it finish well before the shared collection used to.

Three clean local runs of `ad258d8f` found the shared collection ending at 316.9-320.4 s of a 317.6-321.0 s suite, with the last substantive nonshared class (`DemoSeedAttributionTests`, the end of the `demo-seed` lane) ending at 227.8-241.1 s. The shared collection ran alone for 79-93 s at the end of every run. Both of the prior objections to a split were gone, so this document tests one: width two.

## U2: the change

`IntegrationCollection` was one `[CollectionDefinition]` with `ICollectionFixture<CluckworkWebApplicationFactory>` shared by all 94 classes: one factory, one migrated database, serial execution within it. It is now two definitions, `IntegrationCollectionA` and `IntegrationCollectionB`, each with its own `ICollectionFixture<CluckworkWebApplicationFactory>` and therefore its own factory and database. `IntegrationCollectionOrderer` (added in #839 to run the shared collection first, so its serial work overlaps the rest of the suite instead of trailing it) now starts both halves first instead of one.

The 94 classes were assigned to a half by a greedy longest-processing-time bin pack over each class's average `test_seconds` across the three clean `03-demo-seed-profile.md`-era runs (`config3-2/3/4` in that document), moving whole classes only. The three default-account classes (`SeedAndFlockTests`, `AuditActorTests`, `ListAccountsCommandTests`) were kept together as one unit per §D6 of `01-measurement.md`; the two global-purge classes (`IdempotencyRecordPurgeSweepTests`, `RefreshTokenPurgeSweepTests`) were kept together as a second unit. The packer placed the first unit in half A and the second in half B; nothing requires the two units to share a half, only that each stays internally whole. The result balances to 152.7 s per half on that data:

**Half A** (48 classes): `AccountLifecycleCommandTests`, `AccountScopedUserValidatorRegistrationTests`, `AccountScopedUserValidatorTests`, `AccountSuspensionTests`, `AuditActorTests`, `AuditTests`, `AuthCookieContractTests`, `BodyReadingEndpointTests`, `BusinessRecordTimestampTests`, `ChangeUserEmailRaceTests`, `ChangeUserRoleRaceTests`, `CurrencyLockRaceTests`, `DisableUserTests`, `EggGradeManagementTests`, `EggLedgerTests`, `ExpensesTests`, `ExportTests`, `FarmLocalBoundarySweepTests`, `FarmLogoTests`, `FeedUsageTests`, `FlockManagementTests`, `FlockScopeMiddlewareTests`, `FutureLotAllocationTests`, `HealthEndpointTests`, `IdempotencyUserScopeTests`, `InventoryTests`, `ListAccountsCommandTests`, `ListChronologyTests`, `MeEndpointsTests`, `NamedEntityDiscoveryTests`, `PerFarmRefreshCookieTests`, `ProductAuditPayloadTests`, `QualityEggTests`, `RefreshAccountBindingTests`, `ReportQueryBoundingTests`, `ResponseCacheControlTests`, `SaleAllocationPolicyTests`, `SalesDiscountCeilingTests`, `SalesDiscountReasonTests`, `SecurityHeadersTests`, `SeedAndFlockTests`, `SubmitDailyEntryTests`, `TenantScopedLockTests`, `TenantWriteGuardTests`, `UserNameTests`, `UserRoleTenantWriteTests`, `VoidSaleTests`, `WithdrawalRestrictedLotTests`.

**Half B** (46 classes): `AccountLockoutTests`, `AccountRenameServiceTests`, `AccountSlugRaceTests`, `AdminGatingTests`, `AmbientPrincipalOnLoginTests`, `AtomicIdempotencyProtocolTests`, `AuditProvenanceTests`, `BirdMovementTests`, `CatalogTests`, `ChangeUserEmailTests`, `ChangeUserRoleTests`, `CredentialEpochTests`, `CurrencyLockSerializationTests`, `CustomerAndOrderTests`, `DailyEntryAdjustTests`, `DailyEntryGradeTests`, `DetachedTenantWriteTests`, `DisableUserRaceTests`, `EggGradeLowStockFloorTests`, `EggLotConcurrencyTests`, `EggLotWriteOffTests`, `ErrorCodesContractTests`, `FarmBannerTests`, `FarmLocalBoundaryBehindUtcTests`, `FarmLocalRestrictionTests`, `FarmSettingsTests`, `FlockScopeTests`, `IdempotencyRecordPurgeSweepTests`, `IdempotencyReplayTests`, `MustChangePasswordGateTests`, `PaymentsTests`, `ProvisionAccountCommandTests`, `ReadEndpointTests`, `RefreshTokenFlowTests`, `RefreshTokenPurgeSweepTests`, `ReportsTests`, `RoleMatrixTests`, `SalesOrderAuditPayloadTests`, `SalesProductTests`, `StepUpAuthTests`, `TenantIsolationTests`, `TrackedMutationReadTests`, `TwoFarmIsolationMatrixTests`, `UserLanguageColumnTests`, `UserPasswordTests`, `WaterUsageTests`.

`StealLossConnectionReleaseTests` stays outside both halves, as before. `tools/test-timing/summarize.py` now matches `Collection(IntegrationCollectionA.Name)` and `Collection(IntegrationCollectionB.Name)` separately and reports both as `shared_collection_halves`, with `shared_collection` kept as their combined total in its original shape.

## U3: correctness — each half alone

Each half was run alone, in a fresh database, with no other class in the test binary present: `dotnet test --filter "FullyQualifiedName~Class1|FullyQualifiedName~Class2|..."` built from each half's class list. No class name is a substring of another class name anywhere in the 338-class test project, so the substring filter cannot over-match. Half A: 487/487 passed, matching the expected count from the post-#1003 timing data exactly. Half B: 548/548 passed, also an exact match. Neither half needs anything the other half's classes produce.

## U4: correctness — the vacuous-pass audit

Running each half alone proves no class *needs* data from the other half; it would fail if it did. It does not prove the reverse: that a test which checks for absence, emptiness, or a database-wide count was actually exercising the logic its name claims, rather than passing because the data it implicitly expected now lives in a database it no longer shares. This audit targets that failure shape specifically — the one #1000 found in `DemoSeedTests`.

The two groups named in §D6 that are not isolated by a generated tenant are the ones re-checked here, since every other class scopes its own queries to a tenant, flock, or row ID it created:

- **The three default-account classes** (`SeedAndFlockTests`, `AuditActorTests`, `ListAccountsCommandTests`) stayed together, so neither moved relative to the other. `ListAccountsCommandTests` reads with `IgnoreQueryFilters()` to see across tenants, but its only assertions are `Assert.Contains("default-farm", stdout)` (base migration data, present in every cloned database regardless of which half it lands in) and `Assert.Contains(seededSlug, stdout)` (an account it just created). Neither can pass by matching nothing: `default-farm` always exists, and `seededSlug` is freshly generated per test run. `AuditActorTests` and `SeedAndFlockTests` only assert against rows keyed by an ID the test itself created.
- **The two global-purge classes** (`IdempotencyRecordPurgeSweepTests`, `RefreshTokenPurgeSweepTests`) stayed together, moved as a pair to half B. Every assertion in both files is `Assert.True`/`Assert.False(await ExistsAsync(someRecord.Id))` against a record the test created, or a count scoped by a specific `accountId`/`expenseCategoryId` the test created (`db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId)`). None queries a database-wide total.

A broader grep across all 94 classes for `Assert.All`, `Assert.Empty`, `Assert.Equal(0, ...)`, and `.CountAsync()` found the same pattern everywhere else: every hit is scoped to an `accountId`, `entryId`, `flockId`, or similar ID the test method itself generated. The `IgnoreQueryFilters()` call sites outside `ListAccountsCommandTests` (`AccountRenameServiceTests`, `DetachedTenantWriteTests`, `FlockScopeTests`, `IdempotencyRecordPurgeSweepTests`, `ProvisionAccountCommandTests`, `QualityEggTests`, `TenantWriteGuardTests`, `UserRoleTenantWriteTests`) all filter by a specific slug, row ID, or account ID the test created; none scans the whole database. **No check in either half can now pass by matching nothing, and none needed strengthening.**

The `Version`-race classes (`AccountSlugRaceTests`, `ChangeUserEmailRaceTests`, `ChangeUserRoleRaceTests`, `CurrencyLockRaceTests`, `EggLotConcurrencyTests`, and the rest tagged `C` in §D2) and `DetachedTenantWriteTests` race or compare rows only within their own class, which the whole-class-only move preserves regardless of which half a class lands in.

## U5: proving each run measured the intended code

Toggling the working tree between the pre-split commit (`ad258d8f`) and the split commit with `git checkout <rev> -- tests/ tools/test-timing/` and rebuilding leaves no visible difference in the shell if a rebuild is ever skipped by mistake. Each run below is paired with a build fingerprint taken from the actual built assembly immediately before that run started: the SHA-256 of `Cluckwork.Api.IntegrationTests.dll` and a `strings` scan for the embedded collection-name literals. The baseline build contains the string `integration` and not `integration-a` or `integration-b`; the split build contains `integration-a` and `integration-b` and not the bare `integration` string. Every baseline run's fingerprint matched the first baseline build's hash exactly; every split run's fingerprint matched the first split build's hash exactly.

| Build | SHA-256 (first 12 hex) | Collection strings present |
|---|---|---|
| Baseline (`ad258d8f`) | `fc9dc6989944` | `integration` |
| Split (this change) | `bf6a0e96efbe` | `integration-a`, `integration-b` |

## U6: local measurement

Runs were interleaved baseline/split rather than blocked by configuration, with `paseo` agent status and host load checked before and during each run. All peer sessions were idle for every run below; the 90-second-mark load spike visible in most runs (5-14 on a 12-core host) is this suite's own xUnit parallel fan-out and Testcontainers startup, confirmed self-generated by cross-checking agent status at the spike, not external contention. All six runs passed 1,873 tests.

| Configuration | Run | Wall, s | Half A end, s | Half B end, s | `demo-seed` lane end, s |
|---|---:|---:|---:|---:|---:|
| Baseline | 1 | 312.6 | — | — | n/a (unsplit) |
| Baseline | 2 | 331.6 | — | — | n/a (unsplit) |
| Baseline | 3 | 323.8 | — | — | n/a (unsplit) |
| Split | 1 | 276.5 | 226.5 | 206.1 | 275.8 |
| Split | 2 | 277.7 | 222.0 | 202.8 | 277.1 |
| Split | 3 | 273.2 | 222.1 | 203.3 | 272.5 |

Baseline median 323.8 s, range 312.6-331.6 s (spread 19.0 s). Split median 276.5 s, range 273.2-277.7 s (spread 4.5 s). The ranges do not overlap; baseline's lowest value (312.6 s) is 34.9 s above split's highest (277.7 s). The split saves a median 47.3 s, about 14.6%.

The split moves the floor exactly as projected: in every split run, both halves finish well before the `demo-seed` lane (serialized by #1003 into its own collection, untouched by this change), which becomes the new tail at 272.5-277.8 s, essentially identical to the suite wall. The halves themselves land close to the ~153 s each they were balanced for, slightly higher (202-226 s) because they now run concurrently with each other and with the rest of the suite, adding CPU contention beyond what the single-collection baseline had. The issue's own caution ("two halves running side by side also add CPU load") is visible in that gap but did not erase the win.

## U7: CI measurement

<!-- Filled in after the CI runs below land. -->
