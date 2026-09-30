# #863: measure the integration collection before splitting it

**Mode:** measurement. **Base commit:** `ce4a4ab6845c987dbeafe9152fee55a122400d1a` (`origin/main`, checked against the worktree HEAD before either run). **Date:** 2026-09-30.

## D1: current baseline

I ran the full integration project twice on the 12-core local host with real Postgres Testcontainers. The first Release run included restore and build; its VSTest interval is the comparable wall time. The second used the repo's `tools/test-timing/measure.py` after the build. Both passed all 1,873 tests. The local timing files remain in `/tmp/cluckwork-863-measurement/`; the repo's timing tool deliberately keeps raw logs out of Git.

| Measure | #861 figure in issue #863 | Local run 1 | Local run 2 |
|---|---:|---:|---:|
| Integration test wall time | 331.5 s | 396.8 s VSTest | 416.7 s process |
| Shared collection classes | 95 | 96 | 96 |
| Shared collection tests | 1,024 | 1,056 | 1,056 |
| Shared test work, summed | 316.1 s | 381.8 s | 402.4 s |
| Shared first-test to last-test span | 316.1 s | 382.7 s | 403.5 s |
| Other classes | 142 | 148 | 148 |
| Other tests | 791 | 817 | 817 |
| Other test work, summed | 852.4 s | 1,349.9 s | 1,450.9 s |

The checked-out tree has seven more classes and 58 more tests than the issue's #861 run. The new and expanded demo seed work makes summed test durations especially sensitive to local contention. The two local runs differ by 20.6 s in shared work and 101.0 s in other work, despite identical source and test counts. Summed durations are occupied test time, not CPU time, and they overlap.

Current CI gives a different scale. The `Test` steps in the [#997 main run](https://github.com/mforce/cluckwork/actions/runs/36616986439) and [#996 main run](https://github.com/mforce/cluckwork/actions/runs/36618971183) took 564 s and 614 s on `ubuntu-26.04`; their VSTest summaries were 523.3 s and 568.2 s. Parsing the timestamped pass lines gives approximate shared spans of 496.5 s and 539.4 s. Pass-line durations round to milliseconds or whole seconds, and five result lines lack the ordinary class pattern, so these CI figures are corroboration rather than a substitute for TRX. The current runner is slower than this 12-core host and CI timing varies across the two adjacent main runs. The issue's 331.5 s is historical, not today's CI baseline.

Reproduce the local measurement after a Release build with `sg docker -c 'python3 tools/test-timing/measure.py /tmp/cluckwork-863-repeat --configuration Release'`. The committed source has no collection or test change in this pass.

## D2: what the collection shares

`IntegrationCollection` gives every member one `CluckworkWebApplicationFactory`, one migrated Postgres database, and serial execution. The migration supplies the default account, four roles, default grades, and packed-unit conversions. `TestHarness.SeedAccountWithUserAsync` normally creates a new random account and its conversions; its users and feature rows have new IDs. Those generated tenants explain why most classes can move to a different collection with its own database without a row collision. A new collection must get a distinct factory instance and database. Reusing one connection string across concurrent collections changes the test's isolation contract.

The actual shared-state exceptions are narrow. `DemoSeedTests` writes the migration's default account and its demo fixture. `SeedAndFlockTests` adds an Owner to that account. `AuditActorTests` reads it for two guard tests. `ListAccountsCommandTests` reads it through an unscoped CLI query. `AccountProvisioningTests` compares database-wide row counts before and after failed commands. `IdempotencyRecordPurgeSweepTests` and `RefreshTokenPurgeSweepTests` delete eligible rows across accounts. These seven classes need a serial database or their own database. The other 85 writing classes use generated tenants and IDs, with any deliberate race contained inside the class. Four classes only read live metadata or health. No shared test sets `Simulation:*` configuration. No shared test explicitly probes an advisory lock, but every test host registers `DurableJobWorker`, whose `PostgresLeaderLease` takes a session advisory lock before running the global sweeps. Distinct Postgres containers prevent the new hosts from competing for that lease. Several shared members test row locks, which are keyed to their generated rows.

The table names each class's material reads and writes. `I` means generated tenant and row IDs isolate its data from the other shared classes. `C` adds an intentional race within that class; all its racing actors must keep one database. `B` touches the default account. `G` scans or purges across accounts. `R` writes no database row. All classes that log in read the migrated role reference rows. The table lists feature data rather than repeating that common lookup. Durations are summed test seconds from local run 2; they exclude fixture initialization.

| Class | Reads | Writes | Collision | Tests | Work, s |
|---|---|---|---|---:|---:|
| `AccountLifecycleCommandTests` | account, user, token, audit | account status/code, tokens, audit via CLI | I | 24 | 51.6 |
| `AccountLockoutTests` | user lockout | user attempts and lockout | I | 5 | 1.5 |
| `AccountProvisioningTests` | all-account counts, new farm defaults | new accounts, grades, conversions, users, audit | G | 18 | 0.9 |
| `AccountRenameServiceTests` | own account code/version, audit | own account code, audit | C | 9 | 0.7 |
| `AccountScopedUserValidatorRegistrationTests` | DI validator registrations | none | R | 1 | 0.0 |
| `AccountScopedUserValidatorTests` | users in two generated farms | users in those farms | I | 5 | 0.4 |
| `AccountSlugRaceTests` | own account code/version | own account code | C | 3 | 0.2 |
| `AccountSuspensionTests` | own account, user, tokens | account status, tokens, audit | C | 16 | 1.9 |
| `AdminGatingTests` | own roles and protected resources | own users and probe rows | I | 7 | 2.0 |
| `AmbientPrincipalOnLoginTests` | own user, principal, tokens | own account/user/session | I | 6 | 0.9 |
| `AtomicIdempotencyProtocolTests` | own expenses, claim records | own expenses and claims | C | 3 | 0.5 |
| `AuditActorTests` | base account, own audit row | own account/user and audit row | B | 3 | 0.1 |
| `AuditProvenanceTests` | own audit and feature rows | own flocks, entries, orders, audit | I | 48 | 3.6 |
| `AuditTests` | own audit and feature rows | own feature rows and audit | I | 8 | 1.3 |
| `AuthCookieContractTests` | own user and token cookies | own user, refresh tokens | I | 6 | 0.3 |
| `BirdMovementTests` | own flock and movements | own flock, movements | I | 5 | 0.8 |
| `BodyReadingEndpointTests` | live endpoint metadata | none | R | 2 | 0.0 |
| `BusinessRecordTimestampTests` | own customer timestamps | own account, customer | I | 1 | 0.1 |
| `CatalogTests` | own grade, product catalog | own account, grades, products | I | 9 | 1.3 |
| `ChangeUserEmailRaceTests` | own users and identity rows | own user email and claims | C | 12 | 17.1 |
| `ChangeUserEmailTests` | own users, tokens, audit | own user email, tokens, audit | I | 19 | 3.9 |
| `ChangeUserRoleRaceTests` | own users and role assignments | own role assignments | C | 8 | 1.5 |
| `ChangeUserRoleTests` | own users, roles, tokens, audit | own role assignments, tokens, audit | I | 25 | 5.0 |
| `CredentialEpochTests` | own users, epochs, tokens | own passwords, epochs, tokens | I | 14 | 1.9 |
| `CurrencyLockRaceTests` | own account and money rows | own account currency, money rows | C | 9 | 0.4 |
| `CurrencyLockSerializationTests` | own account and expenses | own currency and expenses | C | 4 | 0.1 |
| `CustomerAndOrderTests` | own customers, orders, lots | own customers, orders, allocations | I | 30 | 3.7 |
| `DailyEntryAdjustTests` | own entries, flocks, lots | own entries, grades, lots | I | 13 | 2.9 |
| `DailyEntryGradeTests` | own entries and grade rows | own entries and grade rows | I | 9 | 1.0 |
| `DemoSeedTests` | base farm, grades, owner, demo rows | base owner and full demo fixture | B | 3 | 34.7 |
| `DetachedTenantWriteTests` | two generated farms' rows | attempted detached writes to foreign rows | C | 3 | 0.3 |
| `DisableUserRaceTests` | own users, role links, tokens | own user status, tokens | C | 15 | 4.1 |
| `DisableUserTests` | own users, tokens, audit | own user status, tokens, audit | I | 31 | 7.2 |
| `HealthEndpointTests` | live and ready health | none | R | 1 | 0.0 |
| `EggGradeLowStockFloorTests` | own grades, stock, audit | own grade floor and stock | I | 9 | 1.8 |
| `EggGradeManagementTests` | own grades and audit | own grades and audit | I | 6 | 0.7 |
| `EggLedgerTests` | own lots and movements | own production, sales, movements | I | 6 | 1.1 |
| `EggLotConcurrencyTests` | own lots, orders, movements | own orders and lot allocations | C | 2 | 0.3 |
| `EggLotWriteOffTests` | own lots, movements, audit | own lot write-offs and audit | I | 17 | 2.0 |
| `ErrorCodesContractTests` | own error response | own account and probe row | I | 1 | 0.1 |
| `ExpensesTests` | own expenses and audit | own expenses and audit | I | 5 | 0.8 |
| `ExportTests` | own exported feature rows | own customers, grades, orders | I | 9 | 2.3 |
| `FarmBannerTests` | own banner and account | own banner image and audit | I | 15 | 2.2 |
| `FarmLocalBoundaryBehindUtcTests` | own farm clock and feature rows | own flocks, inventory, water | I | 6 | 3.8 |
| `FarmLocalBoundarySweepTests` | own farm clock, flocks, reports | own flocks and entries | I | 5 | 3.1 |
| `FarmLocalRestrictionTests` | own restricted lot and sale | own lots and sale | I | 2 | 1.2 |
| `FarmLogoTests` | own logo and audit | own logo image and audit | I | 38 | 4.5 |
| `FarmSettingsTests` | own account settings, audit, policy | own settings and probe orders | I | 54 | 7.2 |
| `FeedUsageTests` | own flock, inventory, feed rows | own feed use and inventory | I | 13 | 1.7 |
| `FlockManagementTests` | own flocks and audit | own flock lifecycle and audit | I | 8 | 1.0 |
| `FlockScopeMiddlewareTests` | own users, assignments, flocks | own assignments and probe rows | I | 13 | 1.9 |
| `FlockScopeTests` | two generated farms' scoped rows | own flocks, entries, lots, orders | I | 16 | 5.9 |
| `FutureLotAllocationTests` | own future lot and order | own lot and order | I | 1 | 0.1 |
| `IdempotencyRecordPurgeSweepTests` | all-account claim records | global purge of aged claims | G | 8 | 0.5 |
| `IdempotencyReplayTests` | own claims, entries, expenses | own entries, expenses, claims | I | 3 | 0.3 |
| `IdempotencyUserScopeTests` | own users and claim scope | own account, users, claims | I | 3 | 0.5 |
| `InventoryTests` | own items, lots, movements | own inventory rows | I | 8 | 1.1 |
| `ListAccountsCommandTests` | all accounts, base account | own account and name via CLI | G | 7 | 3.9 |
| `ListChronologyTests` | own chronological lists | own dated rows across modules | I | 1 | 0.2 |
| `MeEndpointsTests` | own user and profile | own profile and language | I | 28 | 3.7 |
| `MustChangePasswordGateTests` | own pending user and gate | own account, user, password | I | 5 | 1.9 |
| `NamedEntityDiscoveryTests` | own names, flocks, customers | own accounts, customers, flocks | I | 53 | 16.4 |
| `PaymentsTests` | own orders and payments | own orders and payments | I | 7 | 4.2 |
| `PerFarmRefreshCookieTests` | own farm cookies and tokens | own accounts, tokens | I | 19 | 7.7 |
| `ProductAuditPayloadTests` | own products and audit | own product and audit | I | 2 | 0.8 |
| `ProvisionAccountCommandTests` | new account by random code | new account, defaults, user via CLI | I | 11 | 22.1 |
| `QualityEggTests` | own entries, grades, lots | own quality grades, entries, lots | I | 12 | 4.4 |
| `ReadEndpointTests` | own feature list/detail rows | own setup and egg-loop rows | I | 13 | 6.1 |
| `RefreshAccountBindingTests` | own accounts, users, tokens | own refresh tokens and logout | I | 4 | 1.5 |
| `RefreshTokenFlowTests` | own user and token lineage | own refresh tokens | I | 7 | 1.5 |
| `RefreshTokenPurgeSweepTests` | all-account refresh tokens | global purge of aged tokens | G | 6 | 1.8 |
| `ReportQueryBoundingTests` | own movement report SQL | own account, flock, 500 movements | I | 2 | 0.6 |
| `ReportsTests` | own production and finance rows | own flocks, entries, orders | I | 22 | 10.6 |
| `ResponseCacheControlTests` | own response headers and rows | own account and probe rows | I | 11 | 2.2 |
| `RoleMatrixTests` | own roles and protected rows | own users and feature probes | I | 18 | 15.2 |
| `SaleAllocationPolicyTests` | own orders, lots, account policy | own account policy and allocations | I | 13 | 11.3 |
| `SalesDiscountCeilingTests` | own account limit, orders, lots | own discount limit and orders | I | 20 | 10.8 |
| `SalesDiscountReasonTests` | own orders and audit | own discounted orders | I | 16 | 7.1 |
| `SalesOrderAuditPayloadTests` | own order items and audit | own orders, items, audit | I | 4 | 1.4 |
| `SalesProductTests` | own products, grades, orders | own product mappings and orders | I | 25 | 10.4 |
| `SecurityHeadersTests` | HTTP security headers | none | R | 8 | 0.1 |
| `SeedAndFlockTests` | base account and own flocks | base Owner user, own flocks | B | 3 | 1.1 |
| `StepUpAuthTests` | own users, roles, grants, audit | own users, grants, assignments | I | 45 | 31.7 |
| `SubmitDailyEntryTests` | own entries, lots, flocks | own submissions, lots, audit | I | 13 | 5.0 |
| `TenantIsolationTests` | two generated farms' rows | own entries and lots | I | 2 | 0.7 |
| `TenantScopedLockTests` | own account and lock keys | own orders and inventory | C | 2 | 1.0 |
| `TenantWriteGuardTests` | generated farms' daily entries | attempted cross-tenant writes | C | 9 | 1.9 |
| `TrackedMutationReadTests` | own flock mutation | own account and flock | I | 2 | 0.4 |
| `TwoFarmIsolationMatrixTests` | two provisioned farms' egg loop | two farms, users, orders, lots | I | 1 | 1.3 |
| `UserLanguageColumnTests` | own user language | own account and user | I | 1 | 0.2 |
| `UserNameTests` | own users and names | own user names | I | 8 | 3.3 |
| `UserPasswordTests` | own users and tokens | own passwords and tokens | I | 12 | 6.1 |
| `UserRoleTenantWriteTests` | two generated farms' role links | attempted cross-tenant role writes | C | 6 | 1.7 |
| `VoidSaleTests` | own orders, allocations, stock | own void and stock ledger | I | 7 | 2.1 |
| `WaterUsageTests` | own flock and water rows | own water use and flock | I | 7 | 2.2 |
| `WithdrawalRestrictedLotTests` | own restricted lot and order | own lot and order | I | 1 | 2.3 |

## D3: a false pass the serialization already hides

`DemoSeedTests.Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes` asserts that the default account starts with zero flocks, then calls `DemoDataSeeder.SeedAsync()` and creates three. `DemoSeedTests.DemoSeed_PopulatesEveryScreen_AndIsIdempotent` calls the same seeder but checks only `IsSuccess` on its first call, not `SeedStatus.Seeded`. The test can therefore pass after another fact populated its entire fixture. In both full local runs, the boot fact ran first and the population fact passed in 0.250 s and 0.234 s. The same order and 0.594 s and 0.512 s population passes appear in the two cited CI runs. On a fresh database, the population fact alone passed but took 18.550 s. Its full-suite pass did not exercise the seeding path that its name claims. There is no `ITestCaseOrderer` for facts in this project. Reversing their order would instead make the boot fact's zero-flock assertion fail. Fix this class's isolation and first-call status assertion before any split.

`AccountProvisioningTests` also gets a pass only under a quiet shared database. Three tests compare unfiltered counts of accounts, grades, conversions, users, and audit rows before and after a rejected command. Concurrent writes by another class would change those counts even if provisioning correctly rolled back. `ListAccountsCommandTests` observes all accounts, and the two purge classes run cross-account deletion; these are additional shared-database interference paths. A per-collection database avoids cross-collection interference, while serialization within each collection preserves the current assumptions. `TestHarness.SeedUserAsync` has an exists-then-create role path, but the migration already supplies the four roles used here, so that branch does not establish a current race.

## D4: modeled collection floors

I kept four default-account readers and writers together and three global count or purge classes together. For each width, I placed the remaining 89 classes by descending local measured work onto the least-loaded collection. This is possible because the 85 generated-tenant writers and four read-only classes have no identified cross-class row dependency. The grouping is a concrete packing of whole classes, not an equal split of test counts. At width two it produces 47 and 49 classes, with 497 and 559 tests. At width three it produces 34, 33, and 29 classes. At width four it produces 25, 26, 23, and 22 classes.

| Collection width | Local work per group, s | Largest group, s | Same groups on CI #997, s | Same groups on CI #996, s |
|---:|---|---:|---|---|
| 1, observed | 402.4 | 402.4 | 470.3 | 510.4 |
| 2 | 201.2, 201.2 | 201.2 | 252.9, 217.4 | 275.4, 235.0 |
| 3 | 134.1, 134.2, 134.2 | 134.2 | 154.4, 169.3, 146.6 | 179.9, 175.8, 154.8 |
| 4 | 100.6, 100.6, 100.6, 100.6 | 100.6 | 125.4, 92.7, 134.5, 117.8 | 151.9, 102.7, 139.1, 116.7 |

These are sums of class test durations assigned to each proposed collection. They are not split-run wall times. Another collection requires another Postgres container, migration, host initialization, and an xUnit worker slot; test durations can change under that load. The timing harness observed 135 containers in the second local run. The existing shared fixture's first test began 12.3 s after test-process start. Additional fixtures can initialize concurrently, but their cost is not zero. The uneven CI columns show why a locally balanced partition is not a timing guarantee.

## D5: the second floor

The other 148 classes account for 817 tests and 1,450.9 s of summed work in local run 2. The six largest were `DemoSeedDisabledOwnerTests` at 190.9 s for one test, `DemoSeedAttributionTests` at 182.8 s for one, `SeedCommandTests` at 151.9 s for eight, `ProcessRoleGuardTests` at 81.4 s, `OtlpSubprocessExporterTests` at 69.5 s for 11, and `AccountScopedIdentityMigrationTests` at 59.0 s. `OneShotVerbMinimalConfigTests`, named in the issue, was next at 58.6 s. The two single-test demo classes are the missing large blocks in the issue's older picture. The seed and guard classes also launch subprocesses or create databases. None can be shortened by moving a different class out of `integration`.

In local run 2, the last substantive nonshared class ended 222.2 s after process start; the shared collection ended at 415.8 s. The pure `EnvironmentMutatingCollection` ran its near-zero-duration checks at the end because it disables assembly parallelism, so its late timestamp is not another 416 s workload. A width-two group would have about 201 s of tests plus fixture startup. On this host, the nonshared side would then be the floor, around 222 s if its schedule stayed fixed. Widths three and four lower the modeled shared group to 134 s and 101 s but do not lower that other side.

The CI result matters more than the local projection. In the adjacent main runs, the last substantive nonshared class, `SeedCommandTests`, finished only 86.8 s and 45.3 s before the full suite. Holding that observed nonshared schedule fixed, a width-two partition already falls below it; widths three and four cannot improve the end time. This conditional model leaves 436.5 s and 522.9 s of the respective 523.3 s and 568.2 s VSTest intervals, or only 1.20x and 1.09x. An actual split may delay nonshared classes by occupying another xUnit worker slot and starting another database. It may also change contention, so the conditional figures are not a prediction of exact CI savings. The important limit is that the old 331 s to 90 s estimate has no support on current main.

## D6: correctness boundary for a future split

Each proposed collection needs its own migrated Postgres database. `Version` race classes such as `AccountSlugRaceTests`, `ChangeUserEmailRaceTests`, `ChangeUserRoleRaceTests`, `DisableUserRaceTests`, and `EggLotConcurrencyTests` start competing actors inside one fact against the same row. Moving the entire class to another database preserves that race. Sharing one database between newly concurrent collections could introduce unrelated writes that obscure the intended 409 or lock behavior. `DetachedTenantWriteTests` must continue to put both farms in one database so the detached stub actually targets a foreign row. `ReadEndpointTests` and `RoleMatrixTests` must keep their #769 sales paging and access assertions against real SQL and HTTP. `ListChronologyTests` must keep its fixed insertion order and real database-generated sequence. Separate databases preserve these observations; replacing them with mocks or splitting a fact's actors across databases would not.

`StealLossConnectionReleaseTests` already has `SmallPoolIdempotencyFactory` and a one-slot pool. It stays outside every proposed group. No test deletion or higher worker count enters this model.

## Recommendation

**Do not split #863 now.** This measurement found a reproducible false pass in `DemoSeedTests`, and the current CI parallel side finishes within 45 to 87 seconds of the serialized side. The local host suggests a twofold speedup, but the two actual CI runs support only a conditional 1.09x to 1.20x gain before extra fixture and worker-slot costs. Confidence is high in the source sharing map and local measurements, and moderate in the CI timing limit because no split ran on the runner. Repair the demo test and measure CI's nonshared schedule again before changing collections. If that work restores a large shared-only tail, use two isolated collections first; widths three and four have no demonstrated payoff.
