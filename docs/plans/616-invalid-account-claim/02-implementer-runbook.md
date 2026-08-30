# Runbook — #616: reject invalid authenticated account claims before scope resolution

You are an autonomous coding agent with full read, edit, write, and shell tools in the Cluckwork repo
(.NET 10/C#, cwd = `/home/mforce/.codex/worktrees/cluckwork-616`). Execute this runbook top to bottom.
The branch and planning artifacts already exist; implement, verify, commit, push, and open a draft PR.

## Rules

- Transcribe the exact code blocks verbatim. The production block marked **PROTECTED** is
  correctness-critical: transcribe it or stop; never repair, reformat, rename, or improve it. After the
  committed GREEN, the exact temporary M1–M4 edits are the sole exception, authorized only as mutation
  checks; each must be restored byte-for-byte before any following row or gate.
- Run commands exactly as written. An expected RED is clean only when the named tests reach the named
  assertion and show the stable tuple discriminator recorded below. Any compile, discovery, runner,
  infrastructure, or different assertion failure is a stop.
- Every normal gate invocation cites its gate row. Mutation commands intentionally rebuild and therefore
  omit CI's `--no-build`; they are mutation protocol commands, not substituted CI attestations.
- Do not touch `FlockScopeResolutionMiddleware`, `CredentialEpochMiddleware`, JWT validation,
  `TenantContext`, query filters, endpoints, schema/migrations, the SPA/locales, generated `graphify-out/`,
  or any prior plan. Their contracts are explicitly out of scope.
- Files you may edit/create are exactly:
  `src/Cluckwork.Api/Middleware/TenantResolutionMiddleware.cs`,
  `src/Cluckwork.Api/Endpoints/Reports/ReportConcurrencyLimitFilter.cs`, and
  `tests/Cluckwork.Api.IntegrationTests/TenantResolutionMiddlewareTests.cs`.
- Do not rewrite the planning artifacts already committed by the driver.
- Work only on `fix/616-invalid-account-claim`; never merge or enable auto-merge.
- After every mutation restore the file, rebuild, rerun the named test green, and remove every `MUTANT`
  marker. A mutation that does not compile proves nothing and is a stop.
- Run the full test suite in the foreground and report every project summary line verbatim.
- If a protected block conflicts with existing code/tests, stop and report; do not adapt it.

**Protected-block probe — answered by the driver before dispatch:** the block uses only the already-live
authenticated `ClaimsPrincipal`, `FindFirst`, `Guid.TryParse`, and the existing response/return convention.
There is no registration to compose and no interpolated fragment. The driver read the full current
`TenantResolutionMiddleware`, `TenantContext`, `CurrentUserContext`, `FlockScopeResolutionMiddleware`,
and `CredentialEpochMiddlewareOrderTests`; observed `DefaultHttpContext.Response.StatusCode == 200` and
unresolved context defaults in the RED fixture; then compiled the exact block and observed all six new
tests green in the isolated diagnosis worktree on 2026-08-30.

## Verify prerequisites

```bash
test "$(pwd)" = "/home/mforce/.codex/worktrees/cluckwork-616"
test "$(git branch --show-current)" = "fix/616-invalid-account-claim"
test -z "$(git status --porcelain)"
git merge-base --is-ancestor 1690db89f69982fdb1b5a7017c6a0dcdf21787c6 HEAD
dotnet --version
docker --version
node --version
gh auth status
git config --get core.hooksPath
```

Expected versions observed by the driver in this repo/worktree on 2026-08-30: .NET `10.0.302`, Docker
`29.7.2`, Node `v26.7.0`; `gh` authenticated to GitHub. Report version drift but continue; the branch,
cwd, clean-tree, base-ancestry, authentication, and hooks-path checks gate Step 0. `core.hooksPath` must resolve to
`.githooks` or the equivalent absolute path. The opt-in pre-commit hook reads staged paths but runs .NET
tests against the working tree; it is bypassable with `--no-verify`, which this runbook does not use. For
staged C# it runs Domain and Application tests, not API integration tests. The commit-msg hook enforces a
conventional subject and checks the whole stored message.

## Caller ledger

Repo-wide enumeration used by the driver:

```bash
rg -n "TenantResolutionMiddleware|ResolveAccountScope" --glob '!**/bin/**' --glob '!**/obj/**' .
```

The relevant live references are `Program.cs`, `AmbientPrincipalMiddleware`,
`FlockScopeResolutionMiddleware`, `MustChangePasswordMiddleware`, `CredentialEpochMiddleware`,
`AuthEndpoints`, `TenantContext`, `CurrentUserContext`, request/error tests, and the complete-order guard.
Generated/historical `graphify-out/` matches are inventory only and must not be edited.

| Increment | Contract changed | Every production caller | What each does at this commit | Same-commit or later? | Phase 11 observation |
|---|---|---|---|---|---|
| 1 | `TenantResolutionMiddleware.InvokeAsync`: authenticated HTTP principals now require parseable `account_id` before tenant/user/flock resolution | `Program.cs` request pipeline; downstream middleware/endpoints via `next`; direct middleware test harnesses | Ordinary authenticated requests reject missing/malformed account claims with 401 before `next`; valid principals and anonymous requests preserve existing behavior. Login/refresh have their principal erased by `AmbientPrincipalMiddleware` before this middleware and remain anonymous here. | Same commit; no signature change or caller edit | Driver fills after replaying focused tests, order guard, and full suite against the commit |

## Environment expectations

| Expectation | Observed by | On | Gates which step |
|---|---|---|---|
| Base product SHA `1690db89f69982fdb1b5a7017c6a0dcdf21787c6`; planning commit is its single descendant | `git rev-parse`, `git log` | driver feature worktree | prerequisite HEAD check |
| CI run `33323952884` succeeded on the base SHA | `gh run list --commit 1690db... --workflow CI` | GitHub | nothing; report mismatch and continue |
| Baseline full .NET suite is 2072/2072 (361 Domain, 175 Application, 10 AppHost, 1526 API integration) | driver foreground build/test | isolated feature worktree | deltas from G1/G2 |
| Exact RED is expected `(401, 0, False, False)` vs actual `(200, 1, False, True)` for each invalid-account test | driver compiled fixture on base SHA | diagnosis worktree | Step 1a |

## Gate commands

G1 and G2 retain their fixed meanings. Every command is copied from `.github/workflows/ci.yml`; `cd web`
is directory setup for the workflow's `defaults.run.working-directory`, not part of a gate. Run G3 before
G1 because G1 carries `--no-restore`. Image build/scan/smoke, dependency-review, CodeQL, GitGuardian, and
other server-side checks are CI-attested: the image job depends on Actions cache state, a pinned Trivy
action and GitHub credentials, so the implementer must inspect their PR check results rather than claim a
different local command is equivalent.

| ID | Gate | Source | Command, verbatim | Baseline on base SHA | Clean looks like |
|---|---|---|---|---|---|
| G1 | build | `ci.yml`, Build and test / Build | `dotnet build Cluckwork.sln --configuration Release --no-restore` | driver clean: 0 warnings, 0 errors | `Build succeeded.`; 0 warnings/errors |
| G2 | full test | `ci.yml`, Build and test / Test | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | driver clean: 2072/2072 | all four summaries `Failed: 0`; new total 2078 |
| G3 | locked restore | `ci.yml`, Build and test / Restore dependencies | `dotnet restore Cluckwork.sln --locked-mode` | CI-attested on `1690db...`, run `33323952884` | exit 0 |
| G4 | NuGet vulnerability gate | `ci.yml`, Build and test / Audit NuGet dependencies | exact block G4 below | CI-attested on `1690db...`, run `33323952884` | exit 0, no unexcepted high+ advisory |
| G5 | schema docs | `ci.yml`, Build and test / Verify schema docs | `tools/schema-docs/generate.sh --check` | CI-attested on `1690db...`, run `33323952884` | exit 0, no generated diff |
| G6 | vulnerability-gate tests | `ci.yml`, web / Test vulnerability gate | `node --test ../.github/scripts/vuln-gate.test.mjs` | CI-attested on `1690db...`, run `33323952884` | node:test passes |
| G7 | lockfix tests | `ci.yml`, web / Test lockfix classifier | `node --test ../.github/scripts/lockfix.test.mjs` | CI-attested on `1690db...`, run `33323952884` | node:test passes |
| G8 | web install | `ci.yml`, web / Install dependencies | `npm ci` | CI-attested on `1690db...`, run `33323952884` | exit 0 |
| G9 | production npm audit | `ci.yml`, web / Audit prod npm | exact block G9 below | CI-attested on `1690db...`, run `33323952884` | exit 0, no unexcepted high+ advisory |
| G10 | all npm audit, advisory | `ci.yml`, web / Audit all npm | exact block G10 below | CI-attested on `1690db...`, run `33323952884` | exit 0; advisories may be warnings |
| G11 | web coverage | `ci.yml`, web / Test with coverage | `npm run test:coverage` | CI-attested on `1690db...`, run `33323952884` | exit 0, thresholds met |
| G12 | web typecheck/build | `ci.yml`, web / Typecheck and build | `npm run build` | CI-attested on `1690db...`, run `33323952884` | exit 0 |
| G13 | service-worker guard | `ci.yml`, web / Verify service-worker guarantees | `npm run verify:sw` | CI-attested on `1690db...`, run `33323952884` | exit 0 |

G4:

```bash
dotnet list package --vulnerable --include-transitive --format json --output-version 1 \
  | node .github/scripts/vuln-gate.mjs --ecosystem nuget --level high
```

For G6–G13, first `cd web`; after G13, `cd ..`. G9:

```bash
npm audit --omit=dev --json > npm-audit-prod.json || true
node ../.github/scripts/vuln-gate.mjs --ecosystem npm --level high \
  --exceptions ../.github/security-exceptions.json < npm-audit-prod.json
```

G10:

```bash
npm audit --json > npm-audit-all.json || true
node ../.github/scripts/vuln-gate.mjs --ecosystem npm --level moderate --warn-only \
  --exceptions ../.github/security-exceptions.json < npm-audit-all.json
```

CI-only required checks and their source are: dependency review (`ci.yml`, `dependency-review` job),
runtime image build + stale-lock negative guard + pinned Trivy scan + boot/readiness smoke (`ci.yml`,
`image` job), CodeQL (`.github/workflows/codeql.yml`), GitGuardian (GitHub App), and any branch-policy
checks. Clean means the corresponding PR check concludes success. The image job passed in CI run
`33323952884` on the base SHA; CodeQL passed
in separate run `33323952920`; dependency review was skipped because the base event was a push, and no
GitGuardian check-run/status exists on that SHA, so those two are **not driver-verified at baseline** and
must pass on the PR. Do not substitute an invented local command.

## Documentation surfaces

| Surface | Path / key | Locales | Increment | Verification procedure | Phase 11 result |
|---|---|---|---|---|---|
| Maintainer comment made stale by the fix | `ReportConcurrencyLimitFilter.cs`, unresolved-tenant comment | n/a, source comment only | 1 | `rg -n -C 3 -e "No account to partition" -e "authenticated principal" src/Cluckwork.Api/Endpoints/Reports/ReportConcurrencyLimitFilter.cs` must describe the defensive fallback and #616 rejection without claiming invalid authenticated JWTs reach the handler | Driver observed the defensive fallback and #616 rejection wording at head `1415f9f3` |
| User/operator/API/localized surfaces | none — response remains the existing bare 401 and no endpoint/schema/config/UI contract changes | effective locale set irrelevant | 1 | `git diff --name-only origin/main...HEAD` contains no `web/`, endpoint contract, schema, config, or runbook surface beyond this implementation plan | Driver observed no user/operator/API/localized surface at head `1415f9f3` |

## Step 0 — verify branch

The branch already exists and the prerequisite checks must be green. Do not pull, rebase, or recreate it.

===================================================================================
# INCREMENT 1 — reject incomplete authenticated tenant identity
===================================================================================

## 1a. RED — add the causal middleware tests

Create `tests/Cluckwork.Api.IntegrationTests/TenantResolutionMiddlewareTests.cs` with this exact code:

```csharp
namespace Cluckwork.Api.IntegrationTests;

using System.Security.Claims;
using Cluckwork.Api.Middleware;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Hosting;

public sealed class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream()
    {
        var result = await InvokeTenantAsync(
            new Claim("sub", Guid.NewGuid().ToString()));

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: false, UserResolved: false),
            result);
    }

    [Fact]
    public async Task AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream()
    {
        var result = await InvokeTenantAsync(
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("account_id", "not-a-guid"));

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: false, UserResolved: false),
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task AuthenticatedRequest_InvalidSub_IsRejectedBeforeDownstream(string? sub)
    {
        var claims = new List<Claim>
        {
            new("account_id", Guid.NewGuid().ToString()),
        };
        if (sub is not null)
            claims.Add(new Claim("sub", sub));

        var result = await InvokeTenantAsync(claims.ToArray());

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: true, UserResolved: false),
            result);
    }

    [Fact]
    public async Task AuthenticatedRequest_ValidClaims_ResolvesScopesAndContinues()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var flockScope = new FlockScope();
        var finalInvocations = 0;
        var context = AuthenticatedContext(
            new Claim("account_id", accountId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim("email", "owner@test.local"),
            new Claim("role", Roles.Owner));

        await InvokePipelineAsync(context, tenant, user, flockScope, () => finalInvocations++);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, finalInvocations);
        Assert.True(tenant.IsResolved);
        Assert.Equal(accountId, tenant.AccountId);
        Assert.True(user.IsResolved);
        Assert.Equal(userId, user.UserId);
        Assert.Equal("owner@test.local", user.Email);
        Assert.Equal([Roles.Owner], user.Roles);
        Assert.True(flockScope.IsResolved);
        Assert.True(flockScope.IsUnrestricted);
        Assert.Empty(flockScope.AssignedFlockIds);
    }

    [Fact]
    public async Task AnonymousHealthRequest_RemainsUnresolvedAndDoesNotQueryDatabase()
    {
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var flockScope = new FlockScope();
        var finalInvocations = 0;
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";

        await InvokePipelineAsync(context, tenant, user, flockScope, () => finalInvocations++);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, finalInvocations);
        Assert.False(tenant.IsResolved);
        Assert.False(user.IsResolved);
        Assert.True(flockScope.IsResolved);
        Assert.True(flockScope.IsUnrestricted);
        Assert.Empty(flockScope.AssignedFlockIds);
    }

    private static async Task<(int Status, int DownstreamInvocations,
        bool TenantResolved, bool UserResolved)> InvokeTenantAsync(params Claim[] claims)
    {
        var downstreamInvocations = 0;
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var context = AuthenticatedContext(claims);
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            downstreamInvocations++;
            return Task.CompletedTask;
        });
        using var serilog = new LoggerConfiguration().CreateLogger();

        await middleware.InvokeAsync(
            context,
            tenant,
            user,
            new DiagnosticContext(serilog),
            NullLogger<TenantResolutionMiddleware>.Instance);

        return (context.Response.StatusCode, downstreamInvocations,
            tenant.IsResolved, user.IsResolved);
    }

    private static async Task InvokePipelineAsync(
        DefaultHttpContext context,
        TenantContext tenant,
        CurrentUserContext user,
        FlockScope flockScope,
        Action onFinal)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;" +
                "Password=none;Timeout=1;Command Timeout=1")
            .Options;
        await using var db = new AppDbContext(options, tenant, flockScope);
        var flockMiddleware = new FlockScopeResolutionMiddleware(_ =>
        {
            onFinal();
            return Task.CompletedTask;
        });
        var tenantMiddleware = new TenantResolutionMiddleware(
            nextContext => flockMiddleware.InvokeAsync(nextContext, flockScope, user, db));
        using var serilog = new LoggerConfiguration().CreateLogger();

        await tenantMiddleware.InvokeAsync(
            context,
            tenant,
            user,
            new DiagnosticContext(serilog),
            NullLogger<TenantResolutionMiddleware>.Instance);
    }

    private static DefaultHttpContext AuthenticatedContext(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test-authentication")),
    };
}
```

Run this mutation-protocol-style focused command (it must build; do not add `--no-build`):

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream|FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream' \
  --verbosity minimal
```

| Gate row + narrowing | Named tests | Assertion | Stable discriminator | Generated fragments | Fixture seed | Same failure elsewhere | Negative proof |
|---|---|---|---|---|---|---|---|
| G2 semantics, focused command above rebuilds | `...MissingAccountId...` and `...MalformedAccountId...` | equality of `(status, downstream count, tenant resolved, user resolved)` | expected `Tuple (401, 0, False, False)`; actual `Tuple (200, 1, False, True)` | only line numbers/timings | authenticated principal, valid generated `sub`; account absent or literal `not-a-guid` | later credential-epoch middleware also returns 401, but is absent from this direct fixture; downstream count distinguishes it | positive construction assertions are the tuple's `user resolved=True` and `downstream=1` on RED; the real middleware ran because those values can only be set there |

Exactly two tests must fail and zero pass in this filtered invocation. If either passes or the failure
shape differs, stop.

## 1b. GREEN — apply the protected fix

In `TenantResolutionMiddleware.InvokeAsync`, find:

```csharp
    {
        using var accountScope = ResolveAccountScope(context, tenant, diagnosticContext, logger);
```

Replace it with this **PROTECTED** block:

```csharp
    {
        if (context.User.Identity?.IsAuthenticated == true
            && !Guid.TryParse(context.User.FindFirst("account_id")?.Value, out _))
        {
            // An authenticated HTTP principal must resolve both tenant and actor
            // before flock scope. Unresolved is reserved for anonymous/non-HTTP
            // callers; the complete Tenant -> Flock order is pinned by
            // CredentialEpochMiddlewareOrderTests (#616).
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var accountScope = ResolveAccountScope(context, tenant, diagnosticContext, logger);
```

In `ReportConcurrencyLimitFilter.cs`, find:

```csharp
        // No account to partition by, so there is nothing to meter — fall through
        // and let the handler reject it (unauthenticated, or an authenticated JWT
        // with no usable account_id claim).
```

Replace it with:

```csharp
        // No account to partition by, so there is nothing to meter. This remains a
        // defensive fallback; TenantResolutionMiddleware rejects authenticated HTTP
        // requests with no usable account_id before this filter (#616).
```

## 1c. Build and re-run

Run G3, then G1. Run the focused command from 1a again; both tests must pass. Then run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests' \
  --verbosity minimal
```

Expected: 6 passed, 0 failed. Run G2 in full in the foreground. Expected new total: 2078 passed.

## 1d. Commit Increment 1

```bash
git add src/Cluckwork.Api/Middleware/TenantResolutionMiddleware.cs \
  src/Cluckwork.Api/Endpoints/Reports/ReportConcurrencyLimitFilter.cs \
  tests/Cluckwork.Api.IntegrationTests/TenantResolutionMiddlewareTests.cs
git commit -m "fix(auth): reject invalid account claims"
```

===================================================================================
# MUTATION CHECKS — prove every boundary bites
===================================================================================

Run each row separately. Mark it in place with the exact `// MUTANT M1` through `// MUTANT M7` label.
For each row:

1. apply the exact compiling mutation;
2. run the named focused `dotnet test` without `--no-build` or `--no-restore`;
3. observe only the expected RED;
4. for M1–M6 restore `TenantResolutionMiddleware.cs`; for M7 restore `Program.cs`;
5. run G1, then rerun the named focused command green;
6. record both results.

Input classification: authentication is the closed Boolean set (authenticated included, anonymous
excluded). `account_id` is open-valued: valid is included; missing and literal `not-a-guid` represent two
separate excluded classes, while malformed values are unbounded. `sub` preserves its existing valid vs
missing/malformed boundary. The valid and anonymous tests pass before the new guard and are explicitly
exempt from Step 1a's RED requirement; M3/M4 prove they are non-vacuous.

| # | Kind | Exact mutation | Supplied elsewhere? | Named test | Expected result + failure | Rebuild command run | Observed failure |
|---|---|---|---|---|---|---|---|
| M1 | guard, missing account | Replace the protected `if` condition with `if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("account_id") is { Value: var accountIdClaim } && !Guid.TryParse(accountIdClaim, out _)) // MUTANT M1: admit missing account_id` | n/a, replacement | `AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream` | RED: tuple expected `(401, 0, False, False)`, actual `(200, 1, False, True)`; malformed remains rejected | driver replayed G1, then named test GREEN 1/1 after restore | exact tuple RED; failed 1, passed 0 |
| M2 | guard, malformed account | Replace the protected `if` condition with `if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("account_id") is null) // MUTANT M2: admit malformed account_id` | n/a | `AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream` | RED with the same expected/actual tuple; missing remains rejected | driver replayed G1, then named test GREEN 1/1 after restore | exact tuple RED; failed 1, passed 0 |
| M3 | guard, valid included | Replace the protected condition with `if (context.User.Identity?.IsAuthenticated == true && (!Guid.TryParse(context.User.FindFirst("account_id")?.Value, out _) \|\| Guid.TryParse(context.User.FindFirst("account_id")?.Value, out _))) // MUTANT M3: reject every authenticated account claim` | n/a | `AuthenticatedRequest_ValidClaims_ResolvesScopesAndContinues` | RED: status expected 200, actual 401 (and final invocation remains 0) | driver replayed G1, then named test GREEN 1/1 after restore | exact status RED: expected 200, actual 401 |
| M4 | guard, anonymous excluded | Replace the full two-line protected condition with the exact M4 block below | n/a | `AnonymousHealthRequest_RemainsUnresolvedAndDoesNotQueryDatabase` | RED: status expected 200, actual 401; final invocation expected 1, actual 0 | driver replayed G1, then named test GREEN 1/1 after restore | exact status RED: expected 200, actual 401 |
| M5 | guard, missing sub | Apply exact M5 block below in the invalid-`sub` `else` | n/a | theory row `sub: null` | only `sub: null` RED: expected `(401, 0, True, False)`, actual `(200, 1, True, False)`; `not-a-guid` remains green | driver replayed G1, then theory GREEN 2/2 after restore | exact tuple RED for `null`; failed 1, passed 1 |
| M6 | guard, malformed sub | Apply exact M6 block below in the invalid-`sub` `else` | n/a | theory row `sub: "not-a-guid"` | only `sub: "not-a-guid"` RED with the same tuple mismatch; `null` remains green | driver replayed G1, then theory GREEN 2/2 after restore | exact tuple RED for `not-a-guid`; failed 1, passed 1 |
| M7 | guard, middleware order | In `Program.cs`, apply the exact M7 replacement below; preserve the #388 comment | n/a | `CredentialEpochMiddlewareOrderTests.Program_PinsTheCompleteCredentialGateSequence` | RED: expected order has Tenant then Flock; actual has Flock then Tenant | driver replayed G1, then order guard GREEN 1/1 after restore | exact collection-order RED at position 2; failed 1, passed 0 |

Exact M4 replacement:

```csharp
        // MUTANT M4: reject anonymous principals without account_id
        if (!Guid.TryParse(context.User.FindFirst("account_id")?.Value, out _))
```

For M5, replace the existing 401 assignment and return inside the invalid-`sub` `else` with:

```csharp
                if (sub is null)
                {
                    // MUTANT M5: admit missing sub
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
```

For M6, replace the same block with:

```csharp
                if (sub is not null)
                {
                    // MUTANT M6: admit malformed sub
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
```

For M7, find:

```csharp
app.UseMiddleware<AmbientPrincipalMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
// #388 — flock-scope resolution, after tenant/user resolution and before the
// credential gate. Touches no credential state; position pinned by
// CredentialEpochMiddlewareOrderTests.
app.UseMiddleware<FlockScopeResolutionMiddleware>();
```

Replace temporarily with:

```csharp
app.UseMiddleware<AmbientPrincipalMiddleware>();
// #388 — flock-scope resolution, after tenant/user resolution and before the
// credential gate. Touches no credential state; position pinned by
// CredentialEpochMiddlewareOrderTests.
// MUTANT M7: run flock scope before tenant scope
app.UseMiddleware<FlockScopeResolutionMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
```

Use these exact focused mutation commands (all rebuild; none carries a skip-build/restore flag):

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream' --verbosity minimal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream' --verbosity minimal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_ValidClaims_ResolvesScopesAndContinues' --verbosity minimal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AnonymousHealthRequest_RemainsUnresolvedAndDoesNotQueryDatabase' --verbosity minimal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_InvalidSub_IsRejectedBeforeDownstream' --verbosity minimal
```

Run them one at a time for M1 through M6 in the table's order; M5 and M6 use the same theory-method
filter and must each report exactly one failed and one passed case. For M7 use:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~CredentialEpochMiddlewareOrderTests.Program_PinsTheCompleteCredentialGateSequence' \
  --verbosity minimal
```

The mutation allowance temporarily extends to `Program.cs` only for M7. It must have no diff after
restore. No control row is required because results are read directly from raw `dotnet test` output,
without a script/parser/wrapper. At the end:

```bash
grep -rn MUTANT src tests && exit 1 || true
git status --short
test -z "$(git status --porcelain)"
```

The tree must be clean after every restore; no mutant may remain. Any green
guard mutant is a finding: stop and report it rather than changing the test or mutation.

===================================================================================
# FINAL LOCAL GATES, PUSH, AND PR
===================================================================================

Run G3, G4, G1, G2, and G5 at repo root. Then enter `web/` and run G6 through G13 in order. The full G2
suite must run in the foreground. G10 is advisory by design. Then:

```bash
git status --short
test -z "$(git status --porcelain)"
git log --oneline --decorate origin/main..HEAD
git push -u origin fix/616-invalid-account-claim
gh pr create --draft \
  --title "fix(auth): reject invalid account claims" \
  --body-file docs/plans/616-invalid-account-claim/03-pr-body.md
gh pr checks --watch --fail-fast
```

After the draft PR exists, wait for and inspect every CI-only required check named in the gate section.
Any failure is a stop; do not report the implementation complete while checks are pending.

Report: branch, commit SHA, push result, PR number, G1 tail, every G2
project summary, all remaining gate outcomes, and every mutation RED + restored GREEN. Confirm that you
personally applied the test block, protected product block, and comment block; name any block already
present or applied by someone else. Never merge.

===================================================================================
# REVIEW ROUND 1 CORRECTION — INCREMENT 2, TEST AND EVIDENCE HARDENING
===================================================================================

Classification: **Mechanical**. This increment changes no product behavior or contract. It pins the
already-required bare 401, composes the real Tenant → Flock chain to make the unwanted database attempt
observable, and repairs the executed runbook's evidence/rendering. Its proof mutations are the review;
it earns no design-fix round. The driver applied only the runbook table corrections above; shipped-code
fix budget remains 0. The implementer applies the exact test diff below and commits both files together.

This increment answers four verified round-1 findings:

1. the missing/malformed direct tests did not assert that the 401 was bare;
2. those tests' counted generic delegate did not exercise the actual Tenant → Flock seam or observe a
   `UserRoleAssignments` database attempt;
3. the M1–M7 result cells still read `implementer fills` after execution;
4. the documentation-surface table's regex alternation broke its Markdown column count (the corrected
   command now uses two `-e` arguments, preserving both command and rendering semantics).

Three other Claude follow-ups are rejected as defects in this slice: the unchanged
`ResolveAccountScope` authenticated check already prevents an unauthenticated identity carrying claims
from resolving a tenant; `IgnoresAmbientPrincipalAttribute` is enumerated at exactly login and refresh
and has its own integration guards; and rejecting an authenticated invalid-account principal at logout
is the owner-approved all-authenticated-HTTP contract, while anonymous logout still reaches the endpoint's
idempotent no-selection branch. Do not broaden this increment to those surfaces.

## 2a. Apply this exact test-only diff

Apply this block verbatim to
`tests/Cluckwork.Api.IntegrationTests/TenantResolutionMiddlewareTests.cs`:

```diff
@@
 using Microsoft.EntityFrameworkCore;
 using Microsoft.Extensions.Logging.Abstractions;
+using Npgsql;
 using Serilog;
@@
         Assert.Equal(
             (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
-                TenantResolved: false, UserResolved: false),
+                TenantResolved: false, UserResolved: false,
+                BodyLength: 0L, ContentType: (string?)null),
             result);
@@
         Assert.Equal(
             (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
-                TenantResolved: false, UserResolved: false),
+                TenantResolved: false, UserResolved: false,
+                BodyLength: 0L, ContentType: (string?)null),
             result);
     }
+
+    [Theory]
+    [InlineData(null)]
+    [InlineData("not-a-guid")]
+    public async Task AuthenticatedRequest_InvalidAccountId_DoesNotReachFlockDatabase(string? accountId)
+    {
+        var claims = new List<Claim>
+        {
+            new("sub", Guid.NewGuid().ToString()),
+        };
+        if (accountId is not null)
+            claims.Add(new Claim("account_id", accountId));
+
+        var tenant = new TenantContext();
+        var user = new CurrentUserContext();
+        var flockScope = new FlockScope();
+        var finalInvocations = 0;
+        var context = AuthenticatedContext(claims.ToArray());
+        var databaseAttempted = false;
+
+        try
+        {
+            await InvokePipelineAsync(context, tenant, user, flockScope, () => finalInvocations++);
+        }
+        catch (Exception exception) when (
+            exception is NpgsqlException || exception.InnerException is NpgsqlException)
+        {
+            databaseAttempted = true;
+        }
+
+        Assert.Equal(
+            (Status: StatusCodes.Status401Unauthorized, FinalInvocations: 0,
+                TenantResolved: false, UserResolved: false,
+                FlockResolved: false, DatabaseAttempted: false),
+            (Status: context.Response.StatusCode, FinalInvocations: finalInvocations,
+                TenantResolved: tenant.IsResolved, UserResolved: user.IsResolved,
+                FlockResolved: flockScope.IsResolved, DatabaseAttempted: databaseAttempted));
+    }
@@
         Assert.Equal(
             (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
-                TenantResolved: true, UserResolved: false),
+                TenantResolved: true, UserResolved: false,
+                BodyLength: 0L, ContentType: (string?)null),
             result);
@@
     private static async Task<(int Status, int DownstreamInvocations,
-        bool TenantResolved, bool UserResolved)> InvokeTenantAsync(params Claim[] claims)
+        bool TenantResolved, bool UserResolved,
+        long BodyLength, string? ContentType)> InvokeTenantAsync(params Claim[] claims)
@@
         var user = new CurrentUserContext();
         var context = AuthenticatedContext(claims);
+        context.Response.Body = new MemoryStream();
@@
         return (context.Response.StatusCode, downstreamInvocations,
-            tenant.IsResolved, user.IsResolved);
+            tenant.IsResolved, user.IsResolved,
+            context.Response.Body.Length, context.Response.ContentType);
```

Run GREEN:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests' --verbosity minimal
```

Expected: 8/8 passed. A compile/discovery/infrastructure error is not GREEN.

## 2b. New proof mutations

Run each row separately, restoring `TenantResolutionMiddleware.cs` byte-for-byte, touching it, running
G1, and rerunning the named test GREEN before the next row.

| # | Kind | Exact mutation | Named test | Expected RED | Restored result |
|---|---|---|---|---|---|
| M8 | isolated layer, missing account reaches Flock | Replace the protected condition with the exact M8 line below | `AuthenticatedRequest_InvalidAccountId_DoesNotReachFlockDatabase` | only `accountId: null` fails: expected `(401, 0, False, False, False, False)`, actual `(200, 0, False, True, False, True)`; malformed row passes | implementer fills |
| M9 | isolated layer, malformed account reaches Flock | Replace the protected condition with the exact M9 line below | same theory | only `accountId: "not-a-guid"` fails with the same tuple; null row passes | implementer fills |
| M10 | guard response becomes non-bare | After the protected 401 assignment, insert the exact M10 block below | both direct missing/malformed rejection tests | both fail: expected suffix `(0, null)`, actual `(2, "application/problem+json")`; status/downstream/context fields remain correct | implementer fills |

M8 exact replacement line:

```csharp
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("account_id") is { Value: var accountIdClaim } && !Guid.TryParse(accountIdClaim, out _)) // MUTANT M8: admit missing account_id to flock resolution
```

M9 exact replacement line:

```csharp
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("account_id") is null) // MUTANT M9: admit malformed account_id to flock resolution
```

M10 exact insertion after `context.Response.StatusCode = StatusCodes.Status401Unauthorized;` in the
authenticated invalid-account guard only:

```csharp
            // MUTANT M10: make the rejection non-bare
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{}");
```

M8/M9 command:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_InvalidAccountId_DoesNotReachFlockDatabase' \
  --verbosity minimal
```

M10 command:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream|FullyQualifiedName~TenantResolutionMiddlewareTests.AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream' \
  --verbosity minimal
```

The driver compiled the exact test diff and independently observed all eight tests GREEN, M8 and M9
each RED 1/2 through the exact query-attempt tuple, and M10 RED 2/2 through the exact bare-response tuple.
That probe was restored; it is verification evidence, not implementation attribution.

## 2c. Final gates and commit

After all restores:

```bash
! rg -n 'MUTANT|\[DEBUG-' src tests
dotnet build Cluckwork.sln --configuration Release --no-restore
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~TenantResolutionMiddlewareTests' --verbosity minimal
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal
git status --short
git diff --check
git add docs/plans/616-invalid-account-claim/02-implementer-runbook.md \
  tests/Cluckwork.Api.IntegrationTests/TenantResolutionMiddlewareTests.cs
git commit -m "test(auth): harden invalid account claim guards"
git push origin fix/616-invalid-account-claim
```

Do not modify production files. Report the new full SHA, 8-test focused summary, every project full-suite
summary, M8–M10 exact RED/restored GREEN, `git diff --check`, clean status, push result, and PR check state.
Never merge or enable auto-merge.
