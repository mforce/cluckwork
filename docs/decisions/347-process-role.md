# Process role, not statement order (#347)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).

### What was wrong

The API binary serves HTTP **and** carries five run-then-exit operator verbs — `migrate`, `seed`, `recover-admin`, `bootstrap-admin`, `healthcheck`. Several Production boot guards apply to only one of those roles. Nothing said so. What made a guard serving-only was the **position of its statement** in `Program.cs`: below `CliDispatcher.TryRunAsync`'s return, so a one-shot verb had already exited before reaching it.

That is not a property anyone can read off the guard, and getting it wrong is not a compile error. It went wrong twice:

- **#331 — the one with teeth.** The #316 OTLP endpoint validation ran at *service registration*, ahead of the dispatcher. A plaintext `Otlp:Endpoint` therefore killed `recover-admin` with SIGABRT 134 — the break-glass verb, the one that exists for when everything else is broken, taken out by a guard that protects a serving process's telemetry. The fix at the time was a `!CliDispatcher.IsCliInvocation(args)` bool threaded into registration: correct, but it made the *second* mechanism for the same idea.
- **A latent third.** `IsCliInvocation` was derived from `CliDispatcher.Commands`, which holds only the four verbs that dispatch after `Build()`. `healthcheck` is not among them — it is not an `ICliCommand` (it needs no host, so it takes no `WebApplication`) and `Program.cs` dispatches it before the builder exists. So the predicate classified the container's own health probe as a **serving** process. Harmless only because of that early return, i.e. harmless because of statement position again.

### What shipped

`ProcessRoles.From(args)` (`src/Cluckwork.Api/Hosting/ProcessRole.cs`) computes `ProcessRole.Serving | OneShot` **once**, before `WebApplication.CreateBuilder`, and every role-scoped guard takes it as an argument:

| Guard | Role | Where |
|---|---|---|
| #260 trusted proxies (empty) | `Serving` only | `ServingBootGuards.EnsureServingConfiguration` |
| #319 AllowedHosts | `Serving` only | `ServingBootGuards.EnsureServingConfiguration` |
| #316 OTLP endpoint **and protocol** | `Serving` throws; `OneShot` degrades to export-disabled | `AddCluckworkTelemetry` |
| All of `RateLimitingOptions.Validate` — malformed CIDR, window limits, the #311 queue limit | `Serving` throws; `OneShot` degrades to defaults | `AddCluckworkRateLimiting` |
| `FarmLogo` / `FarmBanner` upload caps | `Serving` only, **by mechanism** | `AddCluckworkFeatures`, via `.ValidateOnStart()` |
| #261/#262 Postgres TLS floor | **both** | `AddCluckworkPersistence`, unconditional |
| #264 tzdata/ICU canary | **both** | `AddCluckworkPersistence`, unconditional |

**Scope the whole subsystem, not the one setting that bit you.** Rows 3 and 4 read as they do because the first pass scoped exactly the setting #331 named and nothing else, which left two live bugs of the same shape — both found in review, both reproduced against the real binary:

- `AddCluckworkTelemetry` role-checked the *endpoint* resolution, but `ParseProtocol()` sat three lines above the `try`. `Otlp:Protocol=bogus` therefore killed `recover-admin` with SIGABRT 134 — **#331 verbatim, still reproducible, immediately next to its own fix.** The #331 regression test stepped straight over it by setting a valid protocol alongside the bad endpoint it was testing.
- `RateLimitingOptions.Validate()` ran at registration with no role check, so a malformed CIDR or a nonzero `ReportsConcurrency:QueueLimit` aborted every verb. That left `RateLimiting:TrustedProxies` **empty** correctly serving-only while the **same key malformed** was hostile to all roles — opposite classifications for one key, visible from nowhere.

Rate limiting is inbound-HTTP machinery a run-then-exit verb never serves, so the whole of its validation is serving-only. Its degrade falls back to **defaults, not the operator's values** — the operator's are the ones just declared unusable. A malformed CIDR now also fails as a named `InvalidOperationException` rather than a bare `FormatException` from inside `IPNetwork.Parse`, which named neither the setting nor the value and (not being an `InvalidOperationException`) also slipped past the role filter.

The last serving-only row is serving-only for a different **reason**: `.ValidateOnStart()` runs from `Host.StartAsync`, and `CliDispatcher` operates on the built host without ever starting it. Those two behave correctly today and are listed because nothing said so — convert either to an eager check inside `AddCluckworkFeatures`, the shape #316 had, and every verb aborts.

Three further details are load-bearing.

**`EnsureServingConfiguration` is called BEFORE the CLI dispatch, on purpose.** Leaving it below would have worked and proved nothing. A serving-only guard sitting *ahead* of the dispatcher and still not touching the verbs is the #331 failure mode disarmed rather than avoided, and it is the worked example the next guard author needs. Move that call again and nothing breaks — that is the point.

**The verb list is derived, not restated.** `OneShotVerbs` reads `CliDispatcher.Commands`, so a sixth verb classifies itself without anyone remembering this file. `healthcheck` is the single name written by hand, because it structurally cannot come from there. Omitting it is the latent bug above.

**The both-roles guards take no `ProcessRole` parameter.** An argument nobody branches on implies a branch that does not exist, and the next reader has to check whether it is ignored deliberately or by accident. Unconditional code states "applies to both" more strongly than a parameter can; this table is where that classification is recorded.

### What was rejected

**A separate `Cluckwork.Cli` assembly**, which #347 originally asked for alongside this. Its stated payoff was "verbs gain unit-level coverage that does not need a web host" — but `ICliCommand.RunAsync` takes a `WebApplication`, so a class library holding the same files still needs a fully built web app to run anything. The move delivers the churn and none of the benefit. Getting the benefit means changing the verb signature, which is a different and larger change, and one [#423](https://github.com/mforce/cluckwork/pull/423)'s Phase 10 ("convert CLI verbs to contracts") rewrites anyway.

**An `IBootGuard` collection with a runner.** It reads well and matches how the issue described the target shape, but the thing you would then be trusting is the registration list, and a guard nobody registers silently never runs. Per the [guard-writing rules](407-writing-a-guard.md): prefer the boring guard, because complexity costs double when the complicated thing is the thing you are trusting.

**Converting #316's degrade to a pre-check.** `if (role is Serving) validate…` reads more uniformly, but the current `catch … when (role is OneShot)` writes `warning: OTLP export disabled for this command — <reason>` to stderr. That line is the only thing that tells an operator why a one-shot run is exporting nothing. Skipping validation entirely would silently disable export with no reason attached.

### How it is pinned

`ProcessRoleGuardTests` (integration, subprocess) is the guarantee. Asserting that `migrate` exits 0 under a hostile serving configuration proves nothing unless that configuration genuinely would fail a serving boot — so the suite also has to establish the hostility, and **how it establishes it is the part that took three attempts.**

The first two versions both asserted *"the boot died naming ONE OF these guards"*, and in both the guards run in a fixed order, so the later disjuncts were dead:

- **v1** — three guards, one disjunction. #316 is validated during **service registration**, ahead of `ServingBootGuards`, so the boot always died there. #260 and #319 were never proven hostile at all.
- **v2** — added an arm that satisfied #316, and repeated the mistake one level down. `EnsureServingConfiguration` calls #260 then #319 unconditionally, so the boot always died at #260 and the `AllowedHosts` disjunct was dead. **A mutant deleting the #319 call outright survived every arm.**

Two misses of the same shape mean the method is wrong, not that the list needs another entry ([guard-writing rules](407-writing-a-guard.md)). So v3 has no list of arms. Every serving-only guard is a **row in a table**, and the suite derives **two** arms per row: one runs `migrate` with that guard violated and requires exit 0 (the guarantee), the other starts a serving process with that guard violated and **every other satisfied**, and requires it to die naming that guard **and not naming any other**. No arm ever has two violations to choose between, so guard ordering cannot hide anything, and the negative half turns "a different guard caught it" from silently green into red.

Both directions are per-row, which is not symmetry for its own sake: two rows key on the **same setting in mutually exclusive states** (`RateLimiting:TrustedProxies` absent vs malformed), so a single combined "violate everything" run structurally cannot violate both, and whichever lost would have been covered by nothing — the dead-disjunct shape one level further out. Violations are applied *after* every `Satisfy` for the same reason; the first attempt at that pair failed loudly because a later `Satisfy` un-violated the row under test, which is what the negative assertions are for.

**Rows are per VIOLATION, not per subsystem.** `Otlp:Endpoint` and `Otlp:Protocol` are separate rows because they had separate role behaviour, and that is exactly how the second #331 was found.

**And the table itself is enforced, because it was not.** Deleting the #319 row deleted its arms and the suite went from green to green — so the v2 survivor was re-openable by editing the *test*, and any guard added later was covered by nothing. "Adding a guard is a row" was an invariant living in a comment, which is a bug unless a line enforces it. `ServingGuardCoverageTests` now reflects over the three places a serving-only guard can be added — `ServingBootGuards`' check methods, `RateLimitingOptions`' validation methods, and every `IValidateOptions<>` registered with `.ValidateOnStart()` — and fails when one has no row, in both directions so a rename cannot leave a stale entry claiming coverage.

### Then stop enumerating: assert the property

The table above is still a list, and it kept being one entry short. Five instances of this bug class were found across four review rounds — the #316 endpoint (#331 itself), `ParseProtocol` beside it, the OTLP config **binding** beside that, all of `RateLimitingOptions.Validate`, and its binding — **each found immediately after the previous "scope that subsystem" fix**. Five is not five unlucky misses; it is the method failing, and the answer is not a sixth row.

`OneShotVerbMinimalConfigTests` asserts the property instead: **a verb gets a connection string and its own arguments, and nothing else may stop it.** The child environment is built from scratch, so no ambient `Otlp__*`/`Jwt__*` can mask the defect.

**A guard fires on one of two inputs, and one test catches only one of them.** This was nearly shipped overstated — the file first claimed all five instances "would have been caught here", and a mutation run refuted it:

- **ABSENT config** — #260's empty `TrustedProxies`, #319's missing `AllowedHosts`. Caught by the minimal-config arms, which supply nothing.
- **MALFORMED config** — a bad protocol, an unparseable CIDR, a non-numeric `PermitLimit`, a non-boolean `AllowInsecureEndpoint`. Minimal config is **well-formed by construction**, so binding and validation both succeed and the guard never runs. Hoisting either binding back outside its `try` kills the table's arms and leaves every minimal-config case **green**.

Four of the five instances are the malformed shape, so minimal config alone would have caught **one**. `NoServingOnlySection_CanAbortAVerbByBeingUnparseable` is the other half: one deliberately unparseable value per serving-only section, **one section per case** so no arm hides behind another's throw — the dead-disjunct defect is available here too. The value breaks the **binding** rather than a validation rule, because binding is the outer of the two and the round that found the binding bugs found exactly that shape: validation correctly role-scoped while `Get<T>()` was not.

The section set is **walked, not listed** — every type with a `SectionName` constant in either assembly must be probed or excluded with a stated reason. Two are excluded: `Database:Resilience` (every verb opens the same database, so its retry settings are the verb's own configuration — the one section eagerly bound for both roles on purpose) and `Simulation` (it is `seed --profile simulation`'s own input, where a broken value **must** fail the verb).

The same "list what I thought of" method had also sprung three leaks in how the child process's environment was built. The subprocess inherits the test host's environment and **#260's condition is the ABSENCE of `RateLimiting__*`**, so v2 stripped that prefix — but the strip was `Ordinal` while Linux environment keys are case-sensitive and .NET configuration keys are not (`ratelimiting__…` survived it), `ASPNETCORE_`- and `DOTNET_`-prefixed variables bind to the same configuration keys with the prefix removed, and `Otlp__` was never stripped at all — so an inherited `Otlp__AllowInsecureEndpoint` (a documented sim-harness setting) would have silently voided the #316 arm. The child environment is now **built from scratch**: cleared, then a small allow-list of OS variables, then every application setting stated explicitly.

One smaller thing from the same review, same family: `SeedCommandRunner`'s timeout message asserted the seed suites' regression ("falling through into `app.Run()`"), which for the serving arms means the exact opposite — a timeout there means the boot *succeeded* — so the message is now per-caller.

`ProcessRoleRegistryTests` (fast, no host) covers the classification itself, including the one case the subprocess suite structurally cannot reach — `healthcheck` returns before a host exists, so no boot guard can observe its role today. Note what it does **not** do: `OneShotVerbs` is derived from `CliDispatcher.Commands`, so walking that list cannot go red when a verb is added or removed. That property comes from the production derivation; the test catches `From` itself regressing.

**The `healthcheck` classification is enforced, not merely asserted.** Its early return means no production path can observe its role — and the safety direction *inverted* in this change: the retired `IsCliInvocation` called it `Serving` (fail closed — a misconfigured Production deploy aborted at a guard), where `OneShot` fails open (guards skipped, `TryRunAsync` finds no match, execution falls through into `app.Run()` and a health probe tries to become a server). An invariant that only a comment defends is not defended, so `Program.cs` throws if it ever reaches the serving path with a `OneShot` role — unreachable today, red the moment the coupling breaks.

Mutation evidence, run before the claim:

| Mutation | Result |
|---|---|
| baseline, unmutated | **green** |
| `EnsureServingConfiguration` drops its `role` check | **red** (`migrate` aborts on #260; 9 of 12 property cases) |
| #316's degrade filter flipped to `Serving` | **red** — #331 verbatim |
| `healthcheck` removed from `OneShotVerbs` | **red** (registry suite) |
| the #319 `AllowedHosts` guard deleted outright | **red** — *survived v2 entirely* |
| the #260 `TrustedProxies` guard neutered | **red** — *survived v2 entirely* |
| rate-limiting validation's role scope reverted | **red** — the live bug above |
| a row deleted from the table | **red** — *was silent before the coverage suite* |
| `ParseProtocol` moved back outside the role-checked `try` | **red** — the other live bug above |
| OTLP `Get<OtlpOptions>()` moved back outside the `try` | **red** — table arms **and** the `Otlp` hostile case |
| rate-limiting `Get<RateLimitingOptions>()` moved back outside it | **red** — table arms **and** the `RateLimiting` hostile case |
| `healthcheck`'s early return removed from `Program.cs` | **red** (the assertion fires, exit 134) |
| restored | **green** |

The marked rows are the ones that matter. The two "survived v2" mutants are how the dead disjuncts were found and are the regression check on the per-guard table. The "row deleted" mutant is the regression check on the coverage suite — before it, deleting a row was green.

Three process notes, because each nearly produced false evidence.

The first mutation script restored with `git checkout --`, which **silently refuses to restore untracked files**. Two of the three targets were new files, so mutants stacked instead of reverting and two "red" results were measured against an already-mutated tree. Snapshot by file copy, and assert the restored tree is green again.

And a mutant being red is not the same as being red *for the stated reason*. Each mutant is re-checked against the specific arm it should redden: the two single-guard rows must fail their own guard's arm — the serving process otherwise boots clean, logging `OTLP export enabled` — and not the one-shot arm, which is the whole claim behind deriving an arm per guard.

Third, and the one that came closest to shipping: **a mutation run refuted a claim already written into the source.** The property test's own header said all five instances "would have been caught here"; the two binding mutants were red overall but left every one of its cases green, because the enumerated arms were doing the killing. Overstating the newer, structurally stronger test is worse than a plain gap — the whole point of adding it was to stop relying on the list, and a reader who believes that claim stops adding rows. It is why the two-shapes distinction above exists at all, and why the hostile-section arms were written. **Run the mutation against the specific test the claim names, not against the suite.**
