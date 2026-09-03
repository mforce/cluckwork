# Fix runbook 2 — #672 review round 2

Same rules as `01-implementer-runbook.md`. Branch `fix/672-fake-otlp-collector` is checked out at
`208a13f3`; keep working on it. Files you may edit: the collector, its test class, and the two runbook
files under `docs/plans/672-fake-otlp-collector/`. Nothing under `src/`.

## Why this exists

Round 2 produced three defects in the fix itself, from three different reviewers, and all three are real.

1. **The absorbing catch also covered the accept.** `GetContextAsync` sat inside the
   `HttpListenerException or IOException` catch, so a listener-level failure — not a client disconnect —
   would be absorbed and the loop would spin on it forever while every waiter timed out with no cause.
   The accept moves out of that catch and reaches `Fault` again.
2. **An export that STALLS is still invisible.** The aborted-export counter only fires when a matched
   export fails or completes. One that merely stalls past the observation window — no reset, no
   completion — is neither published nor aborted, so "no export arrived" passed while an export was
   mid-transfer on the wire. A gauge counts exports in flight and `AssertNoRequestAsync` refuses to
   conclude anything while one is.
3. **The aborted-export test raced the serve loop.** It read the counter after a fixed one-second
   window, and the counter is written on another thread; under load the test could finish first and fail
   on a correct collector. It now waits for the counter with a deadline.

## Increment 7 — the accept faults, a stalled export is visible, the test stops racing

### 7a. RED — add the in-flight guard first — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`. Insert this block
directly ABOVE `    private static int FreeTestPort()`:

```csharp
    [Fact]
    public async Task An_export_still_being_received_is_not_reported_as_no_export()
    {
        using var collector = new FakeOtlpCollector();
        var endpoint = new Uri(collector.Endpoint);

        using var stalled = new TcpClient();
        stalled.Connect(IPAddress.Loopback, endpoint.Port);
        // Complete headers promising a body, then send nothing and hold the connection OPEN. The
        // collector has accepted an export and is blocked reading it: it has neither published nor
        // aborted, and that is exactly the state in which "no export arrived" used to pass.
        var headersOnly = "POST /v1/traces HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\n"u8.ToArray();
        stalled.GetStream().Write(headersOnly);
        stalled.GetStream().Flush();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (collector.ExportsInFlightForTest == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, collector.ExportsInFlightForTest);
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => collector.AssertNoRequestAsync(TimeSpan.FromMilliseconds(50)));

        Assert.Contains("still being received", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, collector.PublishedRequestCountForTest);
    }
```

Run **G1**. It MUST fail to COMPILE — `ExportsInFlightForTest` does not exist yet — exactly as step 4a of
the previous fix runbook did. Record the compiler error and continue to 7b.

### 7b. GREEN — apply the collector changes — PROTECTED

Four edits in `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`.

**Edit 1.** Find this exact block:

```csharp
    private int _abortedExportCount;
    private Exception? _lastAbsorbedConnectionFault;
```

Replace with:

```csharp
    private int _abortedExportCount;
    private int _exportsInFlight;
    private Exception? _lastAbsorbedConnectionFault;
```

**Edit 2.** Find this exact block:

```csharp
    internal int AbortedExportCountForTest => Volatile.Read(ref _abortedExportCount);
```

Replace with:

```csharp
    internal int AbortedExportCountForTest => Volatile.Read(ref _abortedExportCount);

    // An export whose body is still arriving has ALSO arrived. Without this, an export that
    // merely stalls past the observation window — no reset, no completion — is invisible to both
    // the published check and the aborted count, and "no export arrived" passes while one is
    // mid-transfer on the wire.
    internal int ExportsInFlightForTest => Volatile.Read(ref _exportsInFlight);
```

**Edit 3.** Find this exact block:

```csharp
    public async Task AssertNoRequestAsync(TimeSpan observationWindow)
    {
        ThrowIfTerminated();
        var completed = await Task.WhenAny(_terminal.Task, _anyRequest.Task, Task.Delay(observationWindow));
        ThrowIfTerminated();
        Assert.True(completed != _anyRequest.Task,
            "an OTLP request arrived while export was expected to be disabled");
        var aborted = AbortedExportCountForTest;
        Assert.True(aborted == 0,
            $"an OTLP export arrived while export was expected to be disabled but its body never "
            + $"completed, so it was never published ({aborted} aborted; last connection fault: "
            + $"{Volatile.Read(ref _lastAbsorbedConnectionFault)?.Message ?? "none"})");
    }
```

Replace with:

```csharp
    public async Task AssertNoRequestAsync(TimeSpan observationWindow)
    {
        ThrowIfTerminated();
        var completed = await Task.WhenAny(_terminal.Task, _anyRequest.Task, Task.Delay(observationWindow));
        ThrowIfTerminated();
        Assert.True(completed != _anyRequest.Task,
            "an OTLP request arrived while export was expected to be disabled");
        var inFlight = ExportsInFlightForTest;
        Assert.True(inFlight == 0,
            $"an OTLP export was still being received when the observation window closed "
            + $"({inFlight} in flight), so \"no export arrived\" cannot be concluded");
        var aborted = AbortedExportCountForTest;
        Assert.True(aborted == 0,
            $"an OTLP export arrived while export was expected to be disabled but its body never "
            + $"completed, so it was never published ({aborted} aborted; last connection fault: "
            + $"{Volatile.Read(ref _lastAbsorbedConnectionFault)?.Message ?? "none"})");
    }
```

**Edit 4.** Find this exact block:

```csharp
    private async Task ServeAsync()
    {
        HttpListenerContext? currentContext = null;
        try
        {
            while (_listener.IsListening)
            {
                var identifiedAsExport = false;
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

                    identifiedAsExport = true;
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
                    // and faulting here would redden whatever test happens to hold it. But absorbing
                    // is not forgetting: if the request had already been identified as an OTLP export,
                    // count it, so a real export that dies mid-transfer cannot read as "nothing arrived".
                    if (identifiedAsExport) Interlocked.Increment(ref _abortedExportCount);
                    Volatile.Write(ref _lastAbsorbedConnectionFault, ex);
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
                // The accept itself is deliberately OUTSIDE the absorbing catch below. A listener-level
                // failure here is not a client disconnect, and absorbing it would spin this loop
                // forever while every waiter timed out with no cause; it must reach Fault instead.
                currentContext = await _listener.GetContextAsync();

                var identifiedAsExport = false;
                try
                {
                    // Anything that is not an OTLP export is answered and dropped: a local port
                    // scanner's GET / must not read as "the child exported while export was disabled".
                    if (!IsOtlpExport(currentContext.Request))
                    {
                        currentContext.Response.StatusCode = 404;
                        currentContext.Response.Close();
                        currentContext = null;
                        continue;
                    }

                    identifiedAsExport = true;
                    Interlocked.Increment(ref _exportsInFlight);
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
                    // and faulting here would redden whatever test happens to hold it. But absorbing
                    // is not forgetting: if the request had already been identified as an OTLP export,
                    // count it, so a real export that dies mid-transfer cannot read as "nothing arrived".
                    if (identifiedAsExport) Interlocked.Increment(ref _abortedExportCount);
                    Volatile.Write(ref _lastAbsorbedConnectionFault, ex);
                    try { currentContext?.Response.Abort(); } catch { /* already gone */ }
                    currentContext = null;
                }
                finally
                {
                    if (identifiedAsExport) Interlocked.Decrement(ref _exportsInFlight);
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
```

### 7c. Stop the aborted-export test racing the serve loop — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`. Find this exact
block:

```csharp
        // The collector must not fault (that is the dead-client rule), but it must not pretend the
        // export never arrived either: an export-shaped request that died mid-body is still one that
        // arrived, and "no export arrived" has to fail.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => collector.AssertNoRequestAsync(TimeSpan.FromSeconds(1)));
```

Replace with:

```csharp
        // Wait for the serve loop to observe the reset rather than assuming a fixed window is long
        // enough: the counter is written on another thread, and a window that is merely usually long
        // enough is a test that is merely usually right.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (collector.AbortedExportCountForTest == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // The collector must not fault (that is the dead-client rule), but it must not pretend the
        // export never arrived either: an export-shaped request that died mid-body is still one that
        // arrived, and "no export arrived" has to fail.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => collector.AssertNoRequestAsync(TimeSpan.FromMilliseconds(50)));
```

### 7d. Build and run

Run **G1**, then **G2** narrowed with `--filter 'FullyQualifiedName~FakeOtlpCollectorTests'`. Expect
`Passed: 11`.

### 7e. Commit

```bash
git add tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs
git diff --cached --name-only
git commit -m "test(otlp): the accept faults instead of spinning, and an export in flight is visible"
```

## Increment 8 — the two documentation corrections round 2 raised

**Edit 1.** In `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`, the caller-ledger row still
numbers the docs-only increment 4, while the file defines only three. Find this exact line:

```text
| 4 | none — docs only | n/a | n/a | same commit | driver fills at Phase 11 |
```

Replace with:

```text
| 3 | none — docs only | n/a | n/a | same commit | driver fills at Phase 11 |
```

That is the only change to this file: the row describes the docs-only commit, which this runbook's own
Increment 3 makes.

**Edit 2.** In `docs/plans/672-fake-otlp-collector/02-fix-increment-1.md`, the increment headings use
`## Increment N —` where both precedent slices use a bar-wrapped `# INCREMENT N —`. Change each of the
three headings `## Increment 4 —`, `## Increment 5 —`, `## Increment 6 —` to `# INCREMENT 4 —`,
`# INCREMENT 5 —`, `# INCREMENT 6 —`, and change `## Mutation check for this increment` to
`# MUTATION CHECK` and `## Report back` to `# REPORT BACK`. Do not add the `===` divider bars; the
heading level is what the convention turns on.

Then commit, together with this runbook once it is in place:

```bash
git add docs/plans/672-fake-otlp-collector/
git diff --cached --name-only
git commit -m "docs(plans): record the #672 round-2 fix and align the runbook headings"
```

## Increment 9 — gates

Run **G4**, **G1**, **G2** on the whole solution (expect Api.IntegrationTests **1669**), then **G3**.

## Mutation checks

| # | Kind | Mutate | Supplied elsewhere? | Expected test | Expected result + failure | Rebuild command run | Observed failure |
|---|---|---|---|---|---|---|---|
| M7 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace `        var inFlight = ExportsInFlightForTest;` with `        var inFlight = 0; // MUTANT M7: the in-flight arm no longer reports` | n/a — wrong value, not a deletion | `An_export_still_being_received_is_not_reported_as_no_export` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself | G1 after apply and after restore | driver observed exactly that before dispatch |
| M8 | unreachable-in-harness | `FakeOtlpCollector.cs`: move `currentContext = await _listener.GetContextAsync();` back INSIDE the `try` that the absorbing catch guards | n/a — a move, not a deletion | *(none reachable)* | **GREEN, and that is the finding, not a pass.** No test in this harness can synthesise a listener-level accept failure while `IsListening` stays true, so nothing reddens. The change is defensive: it converts an unbounded silent retry into a fault. Record it as a surviving mutant with this reason; do NOT invent a test that appears to cover it | G1 after apply and after restore | driver observed GREEN before dispatch, recorded as unreachable |

Restore, rebuild, confirm `Passed: 11`, and `git grep -n MUTANT -- tests src` returns nothing.

## Report back

The compiler error at 7a, the G1 tail, the four `Test Run Successful.` lines, both mutation rows, which
form of the caller-ledger line you found, and per-increment confirmation that you applied the blocks.
