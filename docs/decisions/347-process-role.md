# Process role, not statement order (#347)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).

## What was wrong

The API binary serves HTTP **and** carries five run-then-exit operator verbs — `migrate`, `seed`, `recover-admin`, `bootstrap-admin`, `healthcheck`. Several Production boot guards apply to only one of those roles. Nothing said so. What made a guard serving-only was the **position of its statement** in `Program.cs`: below `CliDispatcher.TryRunAsync`'s return, so a one-shot verb had already exited before reaching it.

That is not a property anyone can read off the guard, and getting it wrong is not a compile error. It went wrong twice:

- **#331 — the one with teeth.** The #316 OTLP endpoint validation ran at *service registration*, ahead of the dispatcher. A plaintext `Otlp:Endpoint` therefore killed `recover-admin` with SIGABRT 134 — the break-glass verb, the one that exists for when everything else is broken, taken out by a guard that protects a serving process's telemetry. The fix at the time was a `!CliDispatcher.IsCliInvocation(args)` bool threaded into registration: correct, but it made the *second* mechanism for the same idea.
- **A latent third.** `IsCliInvocation` was derived from `CliDispatcher.Commands`, which holds only the four verbs that dispatch after `Build()`. `healthcheck` is not among them — it is not an `ICliCommand` (it needs no host, so it takes no `WebApplication`) and `Program.cs` dispatches it before the builder exists. So the predicate classified the container's own health probe as a **serving** process. Harmless only because of that early return, i.e. harmless because of statement position again.

## What shipped

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

## What was rejected

**A separate `Cluckwork.Cli` assembly**, which #347 originally asked for alongside this. Its stated payoff was "verbs gain unit-level coverage that does not need a web host" — but `ICliCommand.RunAsync` takes a `WebApplication`, so a class library holding the same files still needs a fully built web app to run anything. The move delivers the churn and none of the benefit. Getting the benefit means changing the verb signature, which is a different and larger change, and one [#423](https://github.com/mforce/cluckwork/pull/423)'s Phase 10 ("convert CLI verbs to contracts") rewrites anyway.

**An `IBootGuard` collection with a runner.** It reads well and matches how the issue described the target shape, but the thing you would then be trusting is the registration list, and a guard nobody registers silently never runs. Per the [guard-writing rules](407-writing-a-guard.md): prefer the boring guard, because complexity costs double when the complicated thing is the thing you are trusting.

**Converting #316's degrade to a pre-check.** `if (role is Serving) validate…` reads more uniformly, but the current `catch … when (role is OneShot)` writes `warning: OTLP export disabled for this command — <reason>` to stderr. That line is the only thing that tells an operator why a one-shot run is exporting nothing. Skipping validation entirely would silently disable export with no reason attached.

## How it is pinned

`ProcessRoleGuardTests` (integration, subprocess) is the guarantee, and it is deliberately **two-sided** — asserting that `migrate` exits 0 under a hostile serving configuration proves nothing unless that configuration genuinely would fail a serving boot. Arm 1 runs the real binary with `ASPNETCORE_ENVIRONMENT=Production`, `TrustedProxies` empty, `AllowedHosts=*` and a plaintext `Otlp:Endpoint`, and requires exit 0. Arm 2 starts the same binary with the same environment and **no verb**, and requires it to die with a message naming one of the three guards. Which guard wins is not asserted: #316 throws during registration and the other two after `Build()`, and pinning that order would turn a legitimate reordering into a red test.

`ProcessRoleRegistryTests` (fast, no host) covers the classification itself, including the one case the subprocess suite structurally cannot reach — `healthcheck` returns before a host exists, so no boot guard can observe its role today.

Mutation evidence, run before the claim (baseline green, restore green):

| Mutation | Result |
|---|---|
| `EnsureServingConfiguration` drops its `role` check | **red** (`migrate` aborts on #260) |
| #316's degrade filter flipped to `Serving` | **red** — this is #331 verbatim |
| `healthcheck` removed from `OneShotVerbs` | **red** (registry suite) |

One process note, because it nearly produced false evidence: the first mutation script restored with `git checkout --`, which **silently refuses to restore untracked files**. Two of the three targets were new files, so mutants stacked instead of reverting and two "red" results were measured against an already-mutated tree. Snapshot by file copy, and assert the restored tree is green again.
