# #863: measure the integration collection and repair its false pass

**Mode:** measurement, followed by test repair. **Pre-repair base commit:** `ce4a4ab6845c987dbeafe9152fee55a122400d1a` (`origin/main`, checked against the worktree HEAD before either run). **Date:** 2026-09-30. The post-repair local run used the same backend source after an unrelated web test change on main; the PR branch was then rebased over an unrelated web dependency update.

## D1: pre-repair baseline

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

Reproduce a local measurement after a Release build with `sg docker -c 'python3 tools/test-timing/measure.py /tmp/cluckwork-863-repeat --configuration Release'`. These baseline numbers precede the repair in D7.

## D2: what the collection shared before repair

`IntegrationCollection` gives every member one `CluckworkWebApplicationFactory`, one migrated Postgres database, and serial execution. The migration supplies the default account, four roles, default grades, and packed-unit conversions. `TestHarness.SeedAccountWithUserAsync` normally creates a new random account and its conversions; its users and feature rows have new IDs. Those generated tenants explain why most classes can move to a different collection with its own database without a row collision. A new collection must get a distinct factory instance and database. Reusing one connection string across concurrent collections changes the test's isolation contract.

The actual shared-state exceptions are narrow. `DemoSeedTests` writes the migration's default account and its demo fixture. `SeedAndFlockTests` adds an Owner to that account. `AuditActorTests` reads it for two guard tests. `ListAccountsCommandTests` reads it through an unscoped CLI query. `AccountProvisioningTests` compares database-wide row counts before and after failed commands. `IdempotencyRecordPurgeSweepTests` and `RefreshTokenPurgeSweepTests` delete eligible rows across accounts. These seven classes need a serial database or their own database. The other 85 writing classes use generated tenants and IDs, with any deliberate race contained inside the class. Four classes only read live metadata or health. No shared test sets `Simulation:*` configuration. No shared test explicitly probes an advisory lock, but every test host registers `DurableJobWorker`, whose `PostgresLeaderLease` takes a session advisory lock before running the global sweeps. Distinct Postgres containers prevent the new hosts from competing for that lease. Several shared members test row locks, which are keyed to their generated rows.

The table is the pre-repair census. It names each class's material reads and writes. `I` means generated tenant and row IDs isolate its data from the other shared classes. `C` adds an intentional race within that class; all its racing actors must keep one database. `B` touches the default account. `G` scans or purges across accounts. `R` writes no database row. All classes that log in read the migrated role reference rows. The table lists feature data rather than repeating that common lookup. Durations are summed test seconds from local run 2; they exclude fixture initialization. D7 records the three classes removed from this collection.

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

`DemoSeedTests.Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes` asserts that the default account starts with zero flocks, then calls `DemoDataSeeder.SeedAsync()` and creates three. `DemoSeedTests.DemoSeed_PopulatesEveryScreen_AndIsIdempotent` calls the same seeder but checks only `IsSuccess` on its first call, not `SeedStatus.Seeded`. The test can therefore pass after another fact populated its entire fixture. In both full local runs, the boot fact ran first and the population fact passed in 0.250 s and 0.234 s. The same order and 0.594 s and 0.512 s population passes appear in the two cited CI runs. On a fresh database, the population fact alone passed but took 18.550 s. Its full-suite pass did not exercise the seeding path that its name claims. There is no `ITestCaseOrderer` for facts in this project. Reversing their order would instead make the boot fact's zero-flock assertion fail. D7 records the repair and its red-then-green check.

`AccountProvisioningTests` also gets a pass only under a quiet shared database. Three tests compare unfiltered counts of accounts, grades, conversions, users, and audit rows before and after a rejected command. Concurrent writes by another class would change those counts even if provisioning correctly rolled back. `ListAccountsCommandTests` observes all accounts, and the two purge classes run cross-account deletion; these are additional shared-database interference paths. A per-collection database avoids cross-collection interference, while serialization within each collection preserves the current assumptions. `TestHarness.SeedUserAsync` has an exists-then-create role path, but the migration already supplies the four roles used here, so that branch does not establish a current race.

## D4: pre-repair modeled collection floors

I kept four default-account readers and writers together and three global count or purge classes together. For each width, I placed the remaining 89 classes by descending local measured work onto the least-loaded collection. This is possible because the 85 generated-tenant writers and four read-only classes have no identified cross-class row dependency. The grouping is a concrete packing of whole classes, not an equal split of test counts. At width two it produces 47 and 49 classes, with 497 and 559 tests. At width three it produces 34, 33, and 29 classes. At width four it produces 25, 26, 23, and 22 classes.

| Collection width | Local work per group, s | Largest group, s | Same groups on CI #997, s | Same groups on CI #996, s |
|---:|---|---:|---|---|
| 1, observed | 402.4 | 402.4 | 470.3 | 510.4 |
| 2 | 201.2, 201.2 | 201.2 | 252.9, 217.4 | 275.4, 235.0 |
| 3 | 134.1, 134.2, 134.2 | 134.2 | 154.4, 169.3, 146.6 | 179.9, 175.8, 154.8 |
| 4 | 100.6, 100.6, 100.6, 100.6 | 100.6 | 125.4, 92.7, 134.5, 117.8 | 151.9, 102.7, 139.1, 116.7 |

These are sums of class test durations assigned to each proposed collection. They are not split-run wall times. Another collection requires another Postgres container, migration, host initialization, and an xUnit worker slot; test durations can change under that load. The timing harness observed 135 containers in the second local run. The existing shared fixture's first test began 12.3 s after test-process start. Additional fixtures can initialize concurrently, but their cost is not zero. The uneven CI columns show why a locally balanced partition is not a timing guarantee.

## D5: the pre-repair second floor

The other 148 classes account for 817 tests and 1,450.9 s of summed work in local run 2. The six largest were `DemoSeedDisabledOwnerTests` at 190.9 s for one test, `DemoSeedAttributionTests` at 182.8 s for one, `SeedCommandTests` at 151.9 s for eight, `ProcessRoleGuardTests` at 81.4 s, `OtlpSubprocessExporterTests` at 69.5 s for 11, and `AccountScopedIdentityMigrationTests` at 59.0 s. `OneShotVerbMinimalConfigTests`, named in the issue, was next at 58.6 s. The two single-test demo classes are the missing large blocks in the issue's older picture. The seed and guard classes also launch subprocesses or create databases. None can be shortened by moving a different class out of `integration`.

In local run 2, the last substantive nonshared class ended 222.2 s after process start; the shared collection ended at 415.8 s. The pure `EnvironmentMutatingCollection` ran its near-zero-duration checks at the end because it disables assembly parallelism, so its late timestamp is not another 416 s workload. A width-two group would have about 201 s of tests plus fixture startup. On this host, the nonshared side would then be the floor, around 222 s if its schedule stayed fixed. Widths three and four lower the modeled shared group to 134 s and 101 s but do not lower that other side.

The CI result matters more than the local projection. In the adjacent main runs, the last substantive nonshared class, `SeedCommandTests`, finished only 86.8 s and 45.3 s before the full suite. Holding that observed nonshared schedule fixed, a width-two partition already falls below it; widths three and four cannot improve the end time. This conditional model leaves 436.5 s and 522.9 s of the respective 523.3 s and 568.2 s VSTest intervals, or only 1.20x and 1.09x. An actual split may delay nonshared classes by occupying another xUnit worker slot and starting another database. It may also change contention, so the conditional figures are not a prediction of exact CI savings. The important limit is that the old 331 s to 90 s estimate has no support on current main.

## D6: correctness boundary for a future split

Each proposed collection needs its own migrated Postgres database. `Version` race classes such as `AccountSlugRaceTests`, `ChangeUserEmailRaceTests`, `ChangeUserRoleRaceTests`, `DisableUserRaceTests`, and `EggLotConcurrencyTests` start competing actors inside one fact against the same row. Moving the entire class to another database preserves that race. Sharing one database between newly concurrent collections could introduce unrelated writes that obscure the intended 409 or lock behavior. `DetachedTenantWriteTests` must continue to put both farms in one database so the detached stub actually targets a foreign row. `ReadEndpointTests` and `RoleMatrixTests` must keep their #769 sales paging and access assertions against real SQL and HTTP. `ListChronologyTests` must keep its fixed insertion order and real database-generated sequence. Separate databases preserve these observations; replacing them with mocks or splitting a fact's actors across databases would not.

`StealLossConnectionReleaseTests` already has `SmallPoolIdempotencyFactory` and a one-slot pool. It stays outside every proposed group. No test deletion or higher worker count enters this model.

## D7: repair and post-repair measurement

`DemoSeed_PopulatesEveryScreen_AndIsIdempotent` now requires `SeedStatus.Seeded` on its first call and `SeedStatus.AlreadySeeded` on its second. With that first assertion added but the old shared fixture still in place, the three-fact `DemoSeedTests` run failed: expected `Seeded`, actual `AlreadySeeded` at the first call. The boot fact had seeded the same default account. The red run's TRX is `863-demo-red.trx` in the test project's local `TestResults` directory. After isolation, the focused run passed all 21 demo and provisioning tests.

`DemoSeedBootTests` and the population class each have a distinct `CluckworkWebApplicationFactory` subclass and therefore a distinct migrated Postgres database. A fact's start state no longer depends on the other fact's order, and `SeedAndFlockTests` cannot add an Owner to either database. The farm-clock fact remains with the population class but provisions a new account before seeding it. `AccountProvisioningTests` also gets its own factory: its unfiltered before/after counts now have a database that other classes cannot write. This costs three additional Postgres containers in the full local run (135 before, 138 after). The shared collection now has 94 classes and 1,035 tests. Its remaining default-account members are `SeedAndFlockTests`, `AuditActorTests`, and `ListAccountsCommandTests`; the two cross-account purge classes remain serial there.

The post-repair full Release integration run passed all 1,873 tests. It used the same timing harness and host as local pre-repair run 2. Test times changed markedly under overlapping seeder work: the population class took 230.1 s across its two facts, versus 34.7 s across three facts before repair; `DemoSeedBootTests` took another 160.1 s outside the shared collection. Its population fact itself took 31.4 s, versus 0.234 s in the broken full run and 18.550 s when run alone before repair. These durations include contention, so adding them to the old wall time would be invalid.

| Local measure | Pre-repair run 2 | Post-repair run |
|---|---:|---:|
| Full integration process wall | 416.7 s | 395.3 s |
| Shared class / test count | 96 / 1,056 | 94 / 1,035 |
| Shared test work | 402.4 s | 381.8 s |
| Shared first-test to last-test span | 403.5 s | 383.0 s |
| Nonshared test work | 1,450.9 s | 1,650.0 s |
| Last substantive nonshared finish, after process start | 222.2 s | 279.1 s |
| Shared end minus nonshared finish | 193.6 s | 115.3 s |

For the repaired 94-class collection, I kept the three default-account classes together and the two global-purge classes together. I placed the other 89 classes by descending post-repair local work onto the least-loaded group. The whole-class grouping, including the generated-tenant races, remains as in D4. These are test-work sums, not wall-time forecasts.

| Width | Post-repair local work per group, s | Largest local group, s | Same groups on PR CI 1, s | Same groups on PR CI 2, s |
|---:|---|---:|---|---|
| 1, observed | 381.8 | 381.8 | 496.4 | 499.8 |
| 2 | 190.9, 190.9 | 190.9 | 229.5, 266.9 | 228.9, 270.9 |
| 3 | 127.2, 127.3, 127.3 | 127.3 | 158.1, 151.3, 187.0 | 162.3, 153.7, 183.8 |
| 4 | 95.4, 95.5, 95.4, 95.4 | 95.5 | 107.0, 123.3, 153.1, 113.0 | 108.3, 121.6, 143.4, 126.5 |

The local nonshared schedule now finishes at 279.1 s, after `SeedCommandTests`; its tallest single class is the population class at 230.1 s. A width-two shared load would already fall below that observed schedule before another fixture's startup cost. Holding the nonshared schedule fixed gives a local ceiling of 395.3 / 279.1 = 1.42×. This is conditional: class durations and worker scheduling changed substantially even without a collection split.

Two PR CI runs passed all 1,873 tests on `ubuntu-26.04`: the [repair commit](https://github.com/mforce/cluckwork/actions/runs/36663186072) and the [final documentation commit](https://github.com/mforce/cluckwork/actions/runs/36664157109). Their integration test steps took 604.6 s and 605.9 s, with VSTest summaries of 559.8 s and 559.0 s. Timestamped pass lines give shared spans of 522.8 s and 530.4 s, shared test work of 496.4 s and 499.8 s, and nonshared test work of 1,015.4 s and 1,022.2 s. `SeedCommandTests` was the tallest nonshared class at 148.1 s and 180.2 s; it ended last among substantive nonshared classes in the first run, **9.2 s after** the shared collection. In the second run, `ProcessRoleGuardTests` ended last, **12.5 s before** the shared collection. Both gaps are far smaller than the 45.3–86.8 s shared-only tails in the two pre-repair main runs. The repaired population fact took 34 s on the first PR run and 26 s on the second, versus 0.512–0.594 s in the broken runs. It performed real seed work; the fresh single-fact local run took 18.550 s and the post-repair full local run took 31.4 s.

| CI measure | Pre-repair main #997 | Pre-repair main #996 | PR after repair 1 | PR after repair 2 |
|---|---:|---:|---:|---:|
| Integration test step | 564.0 s | 613.8 s | 604.6 s | 605.9 s |
| VSTest summary | 523.3 s | 568.2 s | 559.8 s | 559.0 s |
| Shared span from pass lines | 496.5 s | 539.4 s | 522.8 s | 530.4 s |
| Shared end minus last substantive nonshared pass | 86.8 s | 45.3 s | −9.2 s | 12.5 s |

The CI group loads use the same post-repair local assignments as the table above. At width two the larger group has 266.9–270.9 s of tests. The observed nonshared schedule leaves no shared-only tail in the first PR run and only 12.5 s in the second. Under the explicit fixed-schedule assumption, widths two, three, and four save **0–12.5 s** of VSTest time, at most about **1.02×**. New fixture startup and worker contention could consume that margin. A split might alter scheduling in either direction, so this is a conditional ceiling, not a measured split-run time. The issue's older claim that 852 s of parallel work fits within the shared block's shadow is false for current CI: the nonshared side either ends after the block or within 12.5 s of it.

## Recommendation

**Do not split #863 now.** The repair made the demo population test exercise its seed path and removed order and global-count dependencies by giving three classes their own databases. The two PR CI runs leave between zero and 12.5 s of shared-only tail, for a conditional width-two ceiling of about 2%; widths three and four have no further modeled payoff. The recommendation changed in strength, not direction: the pre-repair CI runs left 45–87 s of possible tail. Confidence is high that the false pass is fixed (observed red then green, full local and two CI passes), high in the database sharing map, and moderate in the timing decision because no actual split ran. Reopen the split decision only if several future CI runs show a persistent shared-only tail larger than the extra fixture cost.
