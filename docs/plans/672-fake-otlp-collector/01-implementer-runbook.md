# Runbook — #672 (+#676): a collector that survives a lost bind and ignores traffic that is not an OTLP export

You are an autonomous coding agent with FULL tools (read, edit, write, bash) in the `cluckwork` repo
(.NET 10 / C#, React SPA, Postgres via Testcontainers; cwd = repo root). Execute this runbook top to
bottom. You do EVERYTHING: branch, edit, build, test, commit, open the PR.

## Rules

- Transcribe the exact code blocks VERBATIM (comments and whitespace included). Do not reformat, rename,
  or "improve" them. Blocks marked **PROTECTED** are the fix itself: transcribe or stop, never repair.
- Run the commands EXACTLY as given. Do not invent flags.
- After every build/test command, if it is not clean, STOP and fix before continuing. **An expected RED is
  a clean result** — but only that exact RED: the command as written, the named test, failing at the named
  assertion. Anything else is a STOP, however red it looks: a compile error, a runner failure, zero tests
  collected, or a different test failing.
- Every gate command cites its gate row by ID; do not retype a gate command.
- Do NOT touch: anything under `src/` (this slice changes no product code), the other integration test
  classes, `tools/`, `web/`, `docs/decisions/`, `.github/`. Out of scope by owner decision: the three
  sibling probe-then-bind sites (`OtlpSubprocessExporterTests.GetFreeTcpPort`,
  `MultiInstanceIdempotencyTests.cs:100`, `MultiInstanceRateLimitTests.cs:107`).
- Files you may create or edit: `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`,
  `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`,
  `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md` (this file, committed in Increment 4),
  and a scratch `PR-BODY.md` you delete before FINISH. Anything else, STOP and report.
- Work only on the new branch. Never commit to `main`.
- A **mutation check** means: plant a bug on purpose, run the suite, see whether a test notices. RED means
  the test guards the code; GREEN means nothing was watching. Then restore, **rebuild**, re-run.
- Run the FULL suite (G2, exactly as its row records it) in the FOREGROUND and report its final summary
  lines verbatim.
- If a block here conflicts with an existing test, STOP and report. Do not relax or delete that test.

**Protected-block probe (driver, before dispatch):** the two PROTECTED blocks hook `System.Net.HttpListener`.
Probed, not recalled: (1) `HttpListener.Start()` closes the instance on **any** failure — observed in a
throwaway probe (`HttpListenerException (Address already in use)`, then `ObjectDisposedException` from the
same instance) and confirmed in the .NET 10 source (`catch (Exception) { _state = State.Closed; throw; }`),
which is why each attempt must construct a new listener; (2) `HttpListener` cannot bind port 0 —
`http://127.0.0.1:0/` throws `HttpListenerException: Invalid port in prefix` (observed, /tmp/672-port0),
which is why probe-then-bind stays; (3) the accept loop IS entered for a dying client — on the base tree
`A_client_that_dies_mid_request_does_not_fault_the_collector` fails with
`System.Net.HttpListenerException : Unknown error 400` surfaced through `ThrowIfTerminated`, so the new
catch is on the path the mutant M5 re-opens.

**Existing instances of this pattern:** the retry-with-a-fresh-instance shape is novel in this repo — the
other three probe-then-bind sites hand the port to a child process and never bind it in-process. The
method+path gate has no sibling either: this is the only in-process HTTP sink in the suite. Both are
recorded as novel rather than mirrored.

## Verify prerequisites (run first)

```bash
git rev-parse --abbrev-ref HEAD          # expect: main
git status --porcelain                   # expect: empty, or only this runbook file, untracked
git rev-parse HEAD                       # expect: fc0552aef110973be3cee8b369d4c9862045db90
dotnet --version                         # expect: 10.x (driver observed 10.0.100 on this host)
dotnet restore Cluckwork.sln --locked-mode  # this is gate G4; run it HERE so the first
                                         # --no-restore build below has a restored graph.
                                         # expect: exit 0, no NU1004
docker info --format '{{.ServerVersion}}' # expect: a version string — Testcontainers needs the daemon
git config core.hooksPath                # expect: .githooks — the pre-commit hook runs Domain +
                                         # Application unit tests against the WORKING TREE on any .cs
                                         # commit, so never commit a non-compiling tree. Do not use
                                         # --no-verify.
```

## Caller ledger

| Increment | Contract changed | Every caller (repo-wide enumeration) | What each does AT THIS COMMIT | Same-commit or later? | Observed at that commit (Phase 11) |
|---|---|---|---|---|---|
| 1 | `FakeOtlpCollector` gains `internal FakeOtlpCollector(Func<int> portSource)`; the existing parameterless constructor keeps its signature and delegates to it | every `new FakeOtlpCollector()` in the suite: `FakeOtlpCollectorTests.cs` (6 sites), `OtlpSubprocessExporterTests.cs` (8 sites) — enumerated with `git grep -n "new FakeOtlpCollector"` | all compile and behave unchanged: an added overload changes no existing call | same commit | driver fills at Phase 11 |
| 2 | none — no contract change (`ServeAsync` is private) | n/a — private method, no external caller | n/a | same commit | driver fills at Phase 11 |
| 3 | none — no contract change | n/a | n/a | same commit | driver fills at Phase 11 |
| 4 | none — docs only | n/a | n/a | same commit | driver fills at Phase 11 |

## Environment expectations

| Expectation | Observed by | On | Gates which step |
|---|---|---|---|
| Base suite 365 / 10 / 234 / 1664 = 2273, all `Test Run Successful.` | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | driver host, base commit `fc0552ae`, quiet box | G2's baseline; a delta from these counts is the STOP, not absolute green |
| `docs/schema/ is up to date.` | `tools/schema-docs/generate.sh --check` | driver host, base commit | G3 |
| A local port scanner (`moshi-hook serve`) sweeps loopback listeners in bursts on the DRIVER's box | `672-d1-prober.log` | driver host only | nothing — report a mismatch and continue. The fix does not depend on the scanner being present; the new tests send the non-export request themselves. |

## Gate commands

| ID | Gate | Source (path + job/step) | Command, verbatim | Baseline on `fc0552ae` | Clean looks like |
|---|---|---|---|---|---|
| G1 | build | `.github/workflows/ci.yml`, job `Build and test`, step `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean | `    0 Warning(s)` and `    0 Error(s)` — warnings are errors in this repo |
| G2 | test | `.github/workflows/ci.yml`, job `Build and test`, step `Test` | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | Domain 365, AppHost 10, Application 234, Api.IntegrationTests 1664 = 2273, nothing red | four `Test Run Successful.` lines; integration count is 1664 + the 3 tests this slice adds = 1667 |
| G3 | schema docs | `.github/workflows/ci.yml`, job `Build and test`, step `Verify schema docs are current` | `tools/schema-docs/generate.sh --check` | `docs/schema/ is up to date.` | the same line — this slice changes no migration, so a diff here is a STOP |
| G4 | restore, locked mode | `.github/workflows/ci.yml`, job `Build and test`, step `Restore dependencies` | `dotnet restore Cluckwork.sln --locked-mode` | exit 0 | exit 0, no `NU1004` — this slice adds no package |

## Documentation surfaces

| Surface | Path / key | Locales | Increment | Verification procedure | Verified by + SHA |
|---|---|---|---|---|---|
| none — test scaffolding only, no user-visible behaviour | n/a: no SPA string, no glossary term, no Help page entry, no operator runbook step changes; the PR body says so explicitly | n/a | n/a | n/a — nothing renders | n/a |

## Step 0 — branch

```bash
git checkout main
git pull --ff-only
git checkout -b fix/672-fake-otlp-collector
```

===================================================================================
# INCREMENT 1 — a lost port race is retried on a FRESH listener
===================================================================================

**No RED phase, and that is a decision, not an omission.** The test below constructs the collector through
an overload that does not exist on the base commit, so writing it first produces a compile error — which
the rules above define as a STOP, not a red. Its proof that it can fail is the mutation stage: **M1 restores
today's shape (one listener for every attempt) and M2 clamps the retry budget to 1**, and both redden this
test with the two exceptions the issue reported. Do not try to force a red here.

## 1a. Apply the code — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`.

Find this exact line:

```csharp
    private readonly HttpListener _listener = new();
```

Replace with:

```csharp
    private readonly HttpListener _listener;
```

Find this exact block:

```csharp
    public FakeOtlpCollector()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 3)
            {
                // The probe port was claimed before binding; retry a new one.
            }
        }

        _serveTask = Task.Run(ServeAsync);
    }
```

Replace with:

```csharp
    public FakeOtlpCollector() : this(FreePort)
    {
    }

    // The port source is injectable so a test can hand the first attempt a port that is
    // already taken; every other caller in the suite goes through the parameterless constructor.
    internal FakeOtlpCollector(Func<int> portSource)
    {
        ArgumentNullException.ThrowIfNull(portSource);

        for (var attempt = 1; ; attempt++)
        {
            var port = portSource();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Endpoint = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException)
            {
                // Start() closes the listener on ANY failure, so the next attempt needs a
                // fresh instance as well as a fresh port: reusing this one throws
                // ObjectDisposedException from the very next Prefixes access.
                ((IDisposable)listener).Dispose();
                if (attempt >= BindAttempts) throw;
            }
        }

        _serveTask = Task.Run(ServeAsync);
    }

    private const int BindAttempts = 10;
```

## 1b. Add the regression test — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`.

Find this exact line:

```csharp
namespace Cluckwork.Api.IntegrationTests.Infrastructure;
```

Replace with:

```csharp
namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Net;
using System.Net.Sockets;
```

Now insert the block below directly ABOVE the file's FIRST `[Fact]`, which reads
`public async Task Predicate_wait_throws_a_terminal_error_completed_at_the_timeout_catch_boundary()`.
Leave that test and everything after it untouched.

```csharp
    [Fact]
    public async Task Bind_retries_with_a_fresh_listener_after_a_lost_port_race()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var takenPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var answers = new Queue<int>([takenPort]);

        using var collector = new FakeOtlpCollector(() => answers.Count > 0 ? answers.Dequeue() : FreeTestPort());
        using var client = new HttpClient();

        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x01]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));

        Assert.Equal("/v1/traces", captured.Path);
        Assert.DoesNotContain($":{takenPort}", collector.Endpoint, StringComparison.Ordinal);
        (await post).Dispose();
    }

    private static int FreeTestPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

```

## 1c. Build and run

Run **G1**, then **G2** narrowed with `--filter 'FullyQualifiedName~Bind_retries_with_a_fresh_listener'`.
Expect `Passed: 1`. Anything else is a STOP: report the exact message.

## 1d. Commit

```bash
git add tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs
git diff --cached --name-only
git commit -m "test(otlp): retry a lost collector port race on a fresh listener"
```

===================================================================================
# INCREMENT 2 — the collector observes OTLP exports only, and survives a dead client
===================================================================================

Two behaviours in one increment on purpose: both are one contiguous rewrite of `ServeAsync`'s loop body,
so splitting them would make you transcribe an intermediate shape that never ships. The mutation stage
proves each half separately — M3 and M4 for the export gate, M5 for the dead-client skip.

## 2a. RED — add both regression tests first — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`. Insert the block
below directly ABOVE the `FreeTestPort` helper Increment 1 added.

```csharp
    [Fact]
    public async Task Traffic_that_is_not_an_otlp_export_is_answered_and_never_published()
    {
        using var collector = new FakeOtlpCollector();
        using var client = new HttpClient();

        var scan = await client.GetAsync($"{collector.Endpoint}/");
        var wrongPath = await client.PostAsync($"{collector.Endpoint}/", new ByteArrayContent([0x01]));
        var wrongMethod = await client.GetAsync($"{collector.Endpoint}/v1/traces");

        // The captured symptom of #676 comes first, so this test reddens on the bug's own message.
        await collector.AssertNoRequestAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, collector.PublishedRequestCountForTest);
        Assert.Equal(HttpStatusCode.NotFound, scan.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);

        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x02]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x02 }, captured.Body);
        (await post).Dispose();
        scan.Dispose();
        wrongPath.Dispose();
        wrongMethod.Dispose();
    }

    [Fact]
    public async Task A_client_that_dies_mid_request_does_not_fault_the_collector()
    {
        using var collector = new FakeOtlpCollector();
        var endpoint = new Uri(collector.Endpoint);

        using (var rude = new TcpClient())
        {
            rude.Connect(IPAddress.Loopback, endpoint.Port);
            rude.LingerState = new LingerOption(true, 0);
            var truncated = "POST /v1/traces HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\nxx"u8.ToArray();
            rude.GetStream().Write(truncated);
            rude.GetStream().Flush();
        }

        using var client = new HttpClient();
        var post = client.PostAsync($"{collector.Endpoint}/v1/traces", new ByteArrayContent([0x03]));
        var captured = await collector.WaitForRequestAsync("/v1/traces", TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x03 }, captured.Body);
        (await post).Dispose();
    }

```

Run **G1**, then **G2** narrowed with
`--filter 'FullyQualifiedName~Traffic_that_is_not_an_otlp_export|FullyQualifiedName~A_client_that_dies_mid_request'`.
BOTH must fail, and only in these ways:

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|
| G2, filtered as above | `Traffic_that_is_not_an_otlp_export_is_answered_and_never_published` | `AssertNoRequestAsync(TimeSpan.FromMilliseconds(200))` | the message `an OTLP request arrived while export was expected to be disabled`, thrown at `FakeOtlpCollector.AssertNoRequestAsync` | the collector's port number | the collector's own accept loop, entered by three real HTTP requests the test sends (`GET /`, `POST /`, `GET /v1/traces`) — the same path a local port scanner enters | nothing: a fresh collector receives no request until this test sends one | none — the message exists at exactly one call site (`git grep -n "an OTLP request arrived"`) | the three status codes are asserted AFTER the symptom, so the test cannot pass by never sending them, and the later `POST /v1/traces` proves the collector still publishes a real export |
| G2, filtered as above | `A_client_that_dies_mid_request_does_not_fault_the_collector` | the LATER, legitimate `await collector.WaitForRequestAsync("/v1/traces", …)` | `System.Net.HttpListenerException` surfaced through `FakeOtlpCollector.ThrowIfTerminated` (driver observed `Unknown error 400`) | the OS error number may differ on another host; a `TimeoutException` here is NOT the expected red | the accept loop's per-request path, entered by a raw `TcpClient` that sends a truncated `POST /v1/traces` with `Content-Length: 100` and two body bytes, then closes with `LingerState(true, 0)` | nothing: a fresh collector | `Response_completion_failure_faults_without_publishing_the_request` also faults the collector, and is discriminated by exception TYPE (`InvalidOperationException` there, `HttpListenerException` here) | the legitimate export that follows is what proves the collector was faulted rather than merely quiet |

**If either passes, or fails differently, STOP and report — do not continue to 2b.**

## 2b. GREEN — apply the code — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`. Find this exact block:

```csharp
    private async Task ServeAsync()
    {
        HttpListenerContext? currentContext = null;
        try
        {
            while (_listener.IsListening)
            {
                currentContext = await _listener.GetContextAsync();
                using var buffer = new MemoryStream();
                await currentContext.Request.InputStream.CopyToAsync(buffer);
                var headers = currentContext.Request.Headers.AllKeys
                    .Where(key => key is not null)
                    .ToDictionary(key => key!, key => currentContext.Request.Headers[key!] ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);
                var captured = new CapturedOtlpRequest(currentContext.Request.Url!.AbsolutePath, buffer.ToArray(), headers);
                currentContext.Response.StatusCode = 200;
                BeforeResponseCloseForTest?.Invoke();
                currentContext.Response.Close();
                Publish(captured);
                currentContext = null;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException && !_listener.IsListening)
        {
            // Normal disposal interrupts a pending accept.
        }
        catch (Exception ex)
        {
            try { currentContext?.Response.Abort(); } catch { /* already closed */ }
            Fault(ex);
        }
    }
```

Replace with:

```csharp
    private async Task ServeAsync()
    {
        HttpListenerContext? currentContext = null;
        try
        {
            while (_listener.IsListening)
            {
                try
                {
                    currentContext = await _listener.GetContextAsync();

                    // Anything that is not an OTLP export is answered and dropped: a local port
                    // scanner's GET / must not read as "the child exported while export was disabled".
                    if (!IsOtlpExport(currentContext.Request))
                    {
                        currentContext.Response.StatusCode = 404;
                        currentContext.Response.Close();
                        currentContext = null;
                        continue;
                    }

                    using var buffer = new MemoryStream();
                    await currentContext.Request.InputStream.CopyToAsync(buffer);
                    var headers = currentContext.Request.Headers.AllKeys
                        .Where(key => key is not null)
                        .ToDictionary(key => key!, key => currentContext.Request.Headers[key!] ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase);
                    var captured = new CapturedOtlpRequest(currentContext.Request.Url!.AbsolutePath, buffer.ToArray(), headers);
                    currentContext.Response.StatusCode = 200;
                    BeforeResponseCloseForTest?.Invoke();
                    currentContext.Response.Close();
                    Publish(captured);
                    currentContext = null;
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException && _listener.IsListening)
                {
                    // An unrelated client died mid-exchange. That is not this collector's business,
                    // and faulting here would redden whatever test happens to hold it.
                    try { currentContext?.Response.Abort(); } catch { /* already gone */ }
                    currentContext = null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException && !_listener.IsListening)
        {
            // Normal disposal interrupts a pending accept.
        }
        catch (Exception ex)
        {
            try { currentContext?.Response.Abort(); } catch { /* already closed */ }
            Fault(ex);
        }
    }

    private static bool IsOtlpExport(HttpListenerRequest request) =>
        string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
        && request.Url is { AbsolutePath: var path }
        && path.StartsWith("/v1/", StringComparison.Ordinal);
```

## 2c. Build and re-run

Run **G1**, then **G2** narrowed with `--filter 'FullyQualifiedName~FakeOtlpCollectorTests'`. Expect
`Passed: 9` — the six existing cases plus the three this slice adds.

## 2d. Commit

```bash
git add tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs
git diff --cached --name-only
git commit -m "test(otlp): observe OTLP exports only and survive a dead client"
```

===================================================================================
# INCREMENT 3 — full gates, then commit this runbook
===================================================================================

No red phase. Run every gate in the FOREGROUND, in this order, and paste each final line into your report:
**G4**, **G1**, **G2** on the whole solution (expect Domain 365, AppHost 10, Application 234,
Api.IntegrationTests **1667**), then **G3**.

Then commit this runbook where the repo keeps them:

This runbook was delivered into the worktree already at
`docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`, untracked. Do not copy or rewrite it —
just commit the file that is already there. `ls` it first; if it is genuinely absent, STOP and report
rather than reconstructing it from this text.

```bash
ls -l docs/plans/672-fake-otlp-collector/01-implementer-runbook.md
git add docs/plans/672-fake-otlp-collector/01-implementer-runbook.md
git diff --cached --name-only
git commit -m "docs(plans): record the #672 collector runbook"
```

**Shared fixture state at capture:** no shared state — every test in this class constructs its own
collector, and nothing in the class touches the database.

===================================================================================
# MUTATION CHECKS — prove the guards bite
===================================================================================

Apply each mutation, run the NAMED test, restore, **rebuild**, confirm green. Mark every mutant in place
with `// MUTANT M<n>: <what this breaks>` and delete the marker on restore; at the end
`git grep -n MUTANT -- tests src` MUST return nothing. Every row below was run by the driver in a
throwaway worktree before dispatch, so the `Expected` column is an observation, not a prediction — a
result that does not match it is a finding, not a pass. Never pass `--no-build` or `--no-restore` on a
mutation run.

**This table is not a closed-set guard** — `n/a — this guard's input is not a closed set`. The gate's input
is every HTTP request shape, which is open-valued; the rows below cover the two rejected axes (method,
path), the retry's two failure modes, and the connection-level path.

| # | Kind | Mutate | Supplied elsewhere? | Expected test | Expected result + failure | Rebuild command run | Observed failure |
|---|---|---|---|---|---|---|---|
| C | control | `FakeOtlpCollector.cs`: reword the class comment `// Minimal HTTP/protobuf OTLP sink.` — `git grep -n "Minimal HTTP/protobuf"` shows no test reads it | n/a — not a deletion | *(none)* | **GREEN**, 9 passed | G1, both directions | driver saw `Test Run Successful. Total tests: 9` |
| M1 | guard | `FakeOtlpCollector.cs`, three exact edits: (a) replace `            var listener = new HttpListener();` with `            var listener = _reusedForMutant ??= new HttpListener(); // MUTANT M1: one listener for every attempt` followed by `            listener.Prefixes.Clear();`; (b) replace the two catch-body lines `                ((IDisposable)listener).Dispose();` and `                if (attempt >= BindAttempts) throw;` with the single line `                if (attempt >= BindAttempts) throw; // MUTANT M1: no dispose, listener reused`; (c) add the field `    private static HttpListener? _reusedForMutant; // MUTANT M1` directly under `    private const int BindAttempts = 10;` | n/a — wrong value, not a deletion | `Bind_retries_with_a_fresh_listener_after_a_lost_port_race` | **RED** — `System.ObjectDisposedException : Cannot access a disposed object.`, the CI sighting's own shape | G1, both directions | driver saw exactly that |
| M2 | guard | `FakeOtlpCollector.cs`: `private const int BindAttempts = 1;` | n/a — wrong value, not a deletion | `Bind_retries_with_a_fresh_listener_after_a_lost_port_race` | **RED** — `System.Net.HttpListenerException : Address already in use`, the issue's first reported text | G1, both directions | driver saw exactly that |
| M3 | guard | `FakeOtlpCollector.cs`: make the gate accept everything — replace the `IsOtlpExport` body with `request.Url is not null;` | n/a — wrong value, not a deletion | `Traffic_that_is_not_an_otlp_export_is_answered_and_never_published` | **RED** — `an OTLP request arrived while export was expected to be disabled` | G1, both directions | driver saw exactly that. **Note:** `if (false)` does NOT work as this mutant — it trips CS0162 and warnings are errors, so the mutant does not build |
| M4 | guard | `FakeOtlpCollector.cs`: gate on path only — delete the `HttpMethod == "POST"` conjunct from `IsOtlpExport` | n/a — wrong value, not a deletion | `Traffic_that_is_not_an_otlp_export_is_answered_and_never_published` | **RED** — the same message, this time raised by the test's `GET /v1/traces` | G1, both directions | driver saw exactly that |
| M5 | guard | `FakeOtlpCollector.cs`: replace the whole line `                catch (Exception ex) when (ex is HttpListenerException or IOException && _listener.IsListening)` with `                catch (Exception ex) when (false && ex is HttpListenerException) // MUTANT M5: a dead client faults the collector again` — the mutant deliberately drops the `IOException` and `IsListening` conjuncts too; the point is that no connection-level exception is skipped | n/a — wrong value, not a deletion | `A_client_that_dies_mid_request_does_not_fault_the_collector` | **RED** — `System.Net.HttpListenerException : Unknown error 400` through `ThrowIfTerminated` | G1, both directions | driver saw exactly that |

Report the result of every row, including any that did not apply cleanly.

===================================================================================
# FINISH — push + PR
===================================================================================

```bash
git push -u origin fix/672-fake-otlp-collector
```

Open the PR with `gh pr create`, not draft, title exactly:

```text
fix(tests): the OTLP collector survives a lost port race and ignores traffic that is not an export (#672, #676)
```

The body must say, in your own words: the two root causes; that the fix is test scaffolding only and
touches no product code; `Closes #672` and `Closes #676`; that the three sibling probe-then-bind sites
(`OtlpSubprocessExporterTests.GetFreeTcpPort`, `MultiInstanceIdempotencyTests.cs:100`,
`MultiInstanceRateLimitTests.cs:107`) were reviewed and deliberately left alone because a lost race there
fails a child process's own bind and surfaces as a readiness timeout, a different shape with no sighting
on record; and that no user-visible behaviour changed, so no glossary or Help page update is owed.

Report back: branch, PR number, the G1 output tail, the four `Test Run Successful.` lines with their
counts, the result of every mutation row, and — per increment — confirmation that YOU applied its blocks.
