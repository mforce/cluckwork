# Fix runbook 4 — #672: the same lint class in the first runbook

Same rules as `01-implementer-runbook.md`. Branch `fix/672-fake-otlp-collector`, head `04f238a4`.
**Documentation only** — do not touch any `.cs` file.

## Why this exists

The round-3 reviewer reported one MD038 span in `02-fix-increment-1.md`. A finding is a sample, not a
census: a fence-aware scan of every committed runbook in this slice found the same shape twelve more
times, all in `01-implementer-runbook.md`. Fixing only what was reported would leave the class in the
file, so all three affected lines are corrected here. Every code span keeps its content; only the padding
moves outside the span, and where the indentation was load-bearing for an instruction it is now stated in
words.

## Increment 12 — three lines in `docs/plans/672-fake-otlp-collector/01-implementer-runbook.md`

**Line 86.** Find this exact line:

```text
| G1 | build | `.github/workflows/ci.yml`, job `Build and test`, step `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean | `    0 Warning(s)` and `    0 Error(s)` — warnings are errors in this repo |
```

Replace with:

```text
| G1 | build | `.github/workflows/ci.yml`, job `Build and test`, step `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean | the indented lines `0 Warning(s)` and `0 Error(s)` — warnings are errors in this repo |
```

**Line 497.** Find this exact line:

```text
| M1 | guard | `FakeOtlpCollector.cs`, three exact edits: (a) replace `            var listener = new HttpListener();` with `            var listener = _reusedForMutant ??= new HttpListener(); // MUTANT M1: one listener for every attempt` followed by `            listener.Prefixes.Clear();`; (b) replace the two catch-body lines `                ((IDisposable)listener).Dispose();` and `                if (attempt >= BindAttempts) throw;` with the single line `                if (attempt >= BindAttempts) throw; // MUTANT M1: no dispose, listener reused`; (c) add the field `    private static HttpListener? _reusedForMutant; // MUTANT M1` directly under `    private const int BindAttempts = 10;` | n/a — wrong value, not a deletion | `Bind_retries_with_a_fresh_listener_after_a_lost_port_race` | **RED** — `System.ObjectDisposedException : Cannot access a disposed object.`, the CI sighting's own shape | G1, both directions | driver saw exactly that |
```

Replace with:

```text
| M1 | guard | `FakeOtlpCollector.cs`, three exact edits, each on an indented line whose indentation is preserved: (a) replace `var listener = new HttpListener();` with `var listener = _reusedForMutant ??= new HttpListener(); // MUTANT M1: one listener for every attempt` followed by `listener.Prefixes.Clear();`; (b) replace the two catch-body lines `((IDisposable)listener).Dispose();` and `if (attempt >= BindAttempts) throw;` with the single line `if (attempt >= BindAttempts) throw; // MUTANT M1: no dispose, listener reused`; (c) add the field `private static HttpListener? _reusedForMutant; // MUTANT M1` directly under `private const int BindAttempts = 10;` | n/a — wrong value, not a deletion | `Bind_retries_with_a_fresh_listener_after_a_lost_port_race` | **RED** — `System.ObjectDisposedException : Cannot access a disposed object.`, the CI sighting's own shape | G1, both directions | driver saw exactly that |
```

**Line 501.** Find this exact line:

```text
| M5 | guard | `FakeOtlpCollector.cs`: replace the whole line `                catch (Exception ex) when (ex is HttpListenerException or IOException && _listener.IsListening)` with `                catch (Exception ex) when (false && ex is HttpListenerException) // MUTANT M5: a dead client faults the collector again` — the mutant deliberately drops the `IOException` and `IsListening` conjuncts too; the point is that no connection-level exception is skipped | n/a — wrong value, not a deletion | `A_client_that_dies_mid_request_does_not_fault_the_collector` | **RED** — `System.Net.HttpListenerException : Unknown error 400` through `ThrowIfTerminated` | G1, both directions | driver saw exactly that |
```

Replace with:

```text
| M5 | guard | `FakeOtlpCollector.cs`: replace the whole indented line `catch (Exception ex) when (ex is HttpListenerException or IOException && _listener.IsListening)` with `catch (Exception ex) when (false && ex is HttpListenerException) // MUTANT M5: a dead client faults the collector again` — the mutant deliberately drops the `IOException` and `IsListening` conjuncts too; the point is that no connection-level exception is skipped | n/a — wrong value, not a deletion | `A_client_that_dies_mid_request_does_not_fault_the_collector` | **RED** — `System.Net.HttpListenerException : Unknown error 400` through `ThrowIfTerminated` | G1, both directions | driver saw exactly that |
```


## Increment 13 — commit and gates

```bash
git add docs/plans/672-fake-otlp-collector/
git diff --cached --name-only
git commit -m "docs(plans): keep indentation outside the first runbook's inline code spans"
```

Then run **G1** and the full **G2** and paste their results. Nothing should change: expect
Api.IntegrationTests **1669**.

## Report back

The three corrected lines, the G1 tail, the four suite result lines, and the pushed head SHA.
