# Fix runbook 1 — #672 review round 1: an aborted export must not read as "no export arrived"

Same rules as `01-implementer-runbook.md` (read its Rules section again). Branch
`fix/672-fake-otlp-collector` is already checked out at `76af2400`; keep working on it. Files you may
edit: `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`,
`tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`,
`docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`, and this file once it is delivered at
`docs/plans/672-fake-otlp-collector/02-fix-increment-1.md`. Nothing under `src/`.

## Why this exists

Round 1's `false-green` seat found a defect in the fix itself, and it is real. The connection-level catch
added in Increment 2 wraps the WHOLE loop body: `GetContextAsync`, the export gate, the body read and
`Publish`. So a request that already matched the gate — a genuine OTLP export — and then died during
`CopyToAsync` or `Response.Close` is absorbed and never published. `AssertNoRequestAsync` then reports
"no request arrived" while an export demonstrably did. That is the exact false green #676's tests exist to
prevent, reintroduced one layer down by the fix for it.

Absorbing is right; forgetting is not. The fix counts an export-shaped request that died mid-transfer and
makes `AssertNoRequestAsync` fail on it, without faulting the collector (INV-3 stays intact).

Two smaller findings from the same round ride along: the timeout message now names the absorbed fault
instead of dropping it silently, and the gate's http/protobuf-only assumption is stated where the next
person will read it.

## Increment 4 — an aborted export is counted and reported

### 4a. RED — add the guard test first — PROTECTED

Edit `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs`. Insert this block
directly ABOVE `    private static int FreeTestPort()`:

```csharp
    [Fact]
    public async Task An_export_that_dies_mid_transfer_is_reported_not_silently_dropped()
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

        // The collector must not fault (that is the dead-client rule), but it must not pretend the
        // export never arrived either: an export-shaped request that died mid-body is still one that
        // arrived, and "no export arrived" has to fail.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => collector.AssertNoRequestAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("its body never completed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, collector.AbortedExportCountForTest);
        Assert.Equal(0, collector.PublishedRequestCountForTest);
    }
```

Run **G1**, then **G2** narrowed with `--filter 'FullyQualifiedName~An_export_that_dies_mid_transfer'`.

It MUST fail to COMPILE at this point, because `AbortedExportCountForTest` does not exist yet. **That is
the one place in either runbook where a compile error is the expected result** — it is why this step and
4b are one commit. Record the exact compiler error and continue to 4b. If it compiles, STOP and report:
that means the member already existed and this runbook is out of date.

### 4b. GREEN — apply the collector changes — PROTECTED

All five edits are in `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`.

**Edit 1.** Find this exact block:

```csharp
    private Exception? _terminalException;
    private int _publishedRequestCount;
```

Replace with:

```csharp
    private Exception? _terminalException;
    private int _publishedRequestCount;
    private int _abortedExportCount;
    private Exception? _lastAbsorbedConnectionFault;
```

**Edit 2.** Find this exact line:

```csharp
    internal int PublishedRequestCountForTest => Volatile.Read(ref _publishedRequestCount);
```

Replace with:

```csharp
    internal int PublishedRequestCountForTest => Volatile.Read(ref _publishedRequestCount);

    // An export-shaped request whose body never completed still ARRIVED. It is absorbed rather
    // than faulting the collector, but it is counted, because "no export arrived" must not be
    // satisfied by one that arrived and then died mid-transfer.
    internal int AbortedExportCountForTest => Volatile.Read(ref _abortedExportCount);
```

**Edit 3.** Find this exact block:

```csharp
            throw new TimeoutException($"no matching OTLP export arrived on {path} before the timeout");
```

Replace with:

```csharp
            var absorbed = Volatile.Read(ref _lastAbsorbedConnectionFault);
            throw new TimeoutException(
                $"no matching OTLP export arrived on {path} before the timeout"
                + (absorbed is null
                    ? string.Empty
                    : $"; {AbortedExportCountForTest} export(s) died mid-transfer and were absorbed, "
                      + $"last connection fault: {absorbed.Message}"));
```

**Edit 4.** Find this exact block:

```csharp
    public async Task AssertNoRequestAsync(TimeSpan observationWindow)
    {
        ThrowIfTerminated();
        var completed = await Task.WhenAny(_terminal.Task, _anyRequest.Task, Task.Delay(observationWindow));
        ThrowIfTerminated();
        Assert.True(completed != _anyRequest.Task,
            "an OTLP request arrived while export was expected to be disabled");
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
        var aborted = AbortedExportCountForTest;
        Assert.True(aborted == 0,
            $"an OTLP export arrived while export was expected to be disabled but its body never "
            + $"completed, so it was never published ({aborted} aborted; last connection fault: "
            + $"{Volatile.Read(ref _lastAbsorbedConnectionFault)?.Message ?? "none"})");
    }
```

**Edit 5.** Find this exact block:

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

**Edit 6.** Find this exact block:

```csharp
    private static bool IsOtlpExport(HttpListenerRequest request) =>
        string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
        && request.Url is { AbsolutePath: var path }
        && path.StartsWith("/v1/", StringComparison.Ordinal);
```

Replace with:

```csharp
    // http/protobuf ONLY, deliberately: gRPC OTLP uses HTTP/2 and a
    // /opentelemetry.proto.collector.*/Export path, which this gate refuses. A test that points a
    // grpc-protocol child at this collector would see every export 404'd, so do not use this fixture
    // to prove anything about the gRPC transport.
    private static bool IsOtlpExport(HttpListenerRequest request) =>
        string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
        && request.Url is { AbsolutePath: var path }
        && path.StartsWith("/v1/", StringComparison.Ordinal);
```

### 4c. Build and re-run

Run **G1**, then **G2** narrowed with `--filter 'FullyQualifiedName~FakeOtlpCollectorTests'`. Expect
`Passed: 10` — the nine from the first runbook plus the new guard.

### 4d. Commit

```bash
git add tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollectorTests.cs
git diff --cached --name-only
git commit -m "test(otlp): an export that dies mid-transfer is reported, not silently dropped"
```

## Increment 5 — correct the committed runbook's stale cross-reference

Round 1's `repo-rules` seat found that `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`
says the runbook is "committed in Increment 4" while that document defines only three increments. The
runbook is committed in its Increment 3.

Edit `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`. Find this exact text:

```text
  and a scratch `PR-BODY.md` you delete before FINISH. Anything else, STOP and report.
```

The line ABOVE it names Increment 4. Find that exact line:

```text
  `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md` (this file, committed in Increment 4),
```

Replace with:

```text
  `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md` (this file, committed in Increment 3),
```

Then commit, together with this fix runbook once it is in place:

```bash
git add docs/plans/672-fake-otlp-collector/
git diff --cached --name-only
git commit -m "docs(plans): record the #672 fix increment and correct a runbook cross-reference"
```

## Increment 6 — gates

Run **G4**, **G1**, **G2** on the whole solution (expect Api.IntegrationTests **1668**), then **G3**.
Paste each final line into your report.

## Mutation check for this increment

| # | Kind | Mutate | Supplied elsewhere? | Expected test | Expected result + failure | Rebuild command run | Observed failure |
|---|---|---|---|---|---|---|---|
| M6 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace `        var aborted = AbortedExportCountForTest;` with `        var aborted = 0; // MUTANT M6: the aborted-export arm no longer reports` | n/a — wrong value, not a deletion | `An_export_that_dies_mid_transfer_is_reported_not_silently_dropped` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself, since nothing reports the export that arrived and died | G1 after apply and after restore | driver observed exactly that before dispatch |

Restore, rebuild, confirm `Passed: 10`, and `git grep -n MUTANT -- tests src` returns nothing.

## Report back

The compiler error you saw at 4a, the G1 tail, the four `Test Run Successful.` lines, the M6 row, and
per-increment confirmation that you applied the blocks yourself.
