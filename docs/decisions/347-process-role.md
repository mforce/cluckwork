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
| #260 trusted proxies | `Serving` only | `ServingBootGuards.EnsureServingConfiguration` |
| #319 AllowedHosts | `Serving` only | `ServingBootGuards.EnsureServingConfiguration` |
| #316 OTLP endpoint | `Serving` throws; `OneShot` degrades to export-disabled | `AddCluckworkTelemetry` |
| #261/#262 Postgres TLS floor | **both** | `AddCluckworkPersistence`, unconditional |
| #264 tzdata/ICU canary | **both** | `AddCluckworkPersistence`, unconditional |

Three details are load-bearing.

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

Two misses of the same shape mean the method is wrong, not that the list needs another entry ([guard-writing rules](407-writing-a-guard.md)). So v3 has no list of arms. Every serving-only guard is a **row in a table**, and the suite derives one arm per row: violate exactly that guard, **satisfy every other**, and require the boot to die naming that guard **and not naming any other**. No arm ever has two violations to choose between, so guard ordering cannot hide anything, and the negative half turns "a different guard caught it" from silently green into red. Adding a fourth guard is adding a row.

The same "list what I thought of" method had also sprung three leaks in how the child process's environment was built. The subprocess inherits the test host's environment and **#260's condition is the ABSENCE of `RateLimiting__*`**, so v2 stripped that prefix — but the strip was `Ordinal` while Linux environment keys are case-sensitive and .NET configuration keys are not (`ratelimiting__…` survived it), `ASPNETCORE_`- and `DOTNET_`-prefixed variables bind to the same configuration keys with the prefix removed, and `Otlp__` was never stripped at all — so an inherited `Otlp__AllowInsecureEndpoint` (a documented sim-harness setting) would have silently voided the #316 arm. The child environment is now **built from scratch**: cleared, then a small allow-list of OS variables, then every application setting stated explicitly.

One smaller thing from the same review, same family: `SeedCommandRunner`'s timeout message asserted the seed suites' regression ("falling through into `app.Run()`"), which for the serving arms means the exact opposite — a timeout there means the boot *succeeded* — so the message is now per-caller.

`ProcessRoleRegistryTests` (fast, no host) covers the classification itself, including the one case the subprocess suite structurally cannot reach — `healthcheck` returns before a host exists, so no boot guard can observe its role today. Note what it does **not** do: `OneShotVerbs` is derived from `CliDispatcher.Commands`, so walking that list cannot go red when a verb is added or removed. That property comes from the production derivation; the test catches `From` itself regressing.

**The `healthcheck` classification is enforced, not merely asserted.** Its early return means no production path can observe its role — and the safety direction *inverted* in this change: the retired `IsCliInvocation` called it `Serving` (fail closed — a misconfigured Production deploy aborted at a guard), where `OneShot` fails open (guards skipped, `TryRunAsync` finds no match, execution falls through into `app.Run()` and a health probe tries to become a server). An invariant that only a comment defends is not defended, so `Program.cs` throws if it ever reaches the serving path with a `OneShot` role — unreachable today, red the moment the coupling breaks.

Mutation evidence, run before the claim:

| Mutation | Result |
|---|---|
| baseline, unmutated | **green** |
| `EnsureServingConfiguration` drops its `role` check | **red** (`migrate` aborts on #260) |
| #316's degrade filter flipped to `Serving` | **red** — this is #331 verbatim |
| `healthcheck` removed from `OneShotVerbs` | **red** (registry suite) |
| the #319 `AllowedHosts` guard deleted outright | **red** — *survived v2 entirely* |
| the #260 `TrustedProxies` guard neutered | **red** — *survived v2 entirely* |
| `healthcheck`'s early return removed from `Program.cs` | **red** (the assertion fires, exit 134) |
| restored | **green** |

The two marked rows are the ones that matter: under v2 both mutants left every arm green, which is how the dead disjuncts were found. They are the reason the per-guard table exists, and they are the regression check on it.

Two process notes, because each nearly produced false evidence.

The first mutation script restored with `git checkout --`, which **silently refuses to restore untracked files**. Two of the three targets were new files, so mutants stacked instead of reverting and two "red" results were measured against an already-mutated tree. Snapshot by file copy, and assert the restored tree is green again.

And a mutant being red is not the same as being red *for the stated reason*. The fourth was re-run on its own to confirm it fails on arm 3's timeout — the serving process boots clean, logging `OTLP export enabled` — rather than on arm 1 or 2, which is the entire claim behind adding arm 3.
