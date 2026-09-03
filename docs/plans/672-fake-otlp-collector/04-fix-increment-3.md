# Fix runbook 3 — #672 review round 3: markdown lint in the committed runbooks

Same rules as `01-implementer-runbook.md`. Branch `fix/672-fake-otlp-collector` is checked out at
`da07e3aa`. **Documentation only** — do not touch any `.cs` file in this increment.

## Why this exists

Round 3 found no product defect. Its one finding is `markdownlint-cli2` MD038: an inline code span whose
content starts or ends with a space. The reviewer flagged one line; the same shape occurs four times
across the two committed fix runbooks, so all four are corrected here rather than only the one that was
reported. The instruction stays exact — the C# indentation moves outside the code span and is stated in
words, so the implementer still knows the line is indented.

## Increment 10 — four lint corrections

**02-fix-increment-1.md, line 31.** Find this exact line:

```text
directly ABOVE `    private static int FreeTestPort()`:
```

Replace with:

```text
directly ABOVE the indented line `private static int FreeTestPort()`:
```

**02-fix-increment-1.md, line 352.** Find this exact line:

```text
| M6 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace `        var aborted = AbortedExportCountForTest;` with `        var aborted = 0; // MUTANT M6: the aborted-export arm no longer reports` | n/a — wrong value, not a deletion | `An_export_that_dies_mid_transfer_is_reported_not_silently_dropped` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself, since nothing reports the export that arrived and died | G1 after apply and after restore | driver observed exactly that before dispatch |
```

Replace with:

```text
| M6 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace the indented line `var aborted = AbortedExportCountForTest;` with `var aborted = 0; // MUTANT M6: the aborted-export arm no longer reports`, keeping its eight spaces of indentation | n/a — wrong value, not a deletion | `An_export_that_dies_mid_transfer_is_reported_not_silently_dropped` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself, since nothing reports the export that arrived and died | G1 after apply and after restore | driver observed exactly that before dispatch |
```

**03-fix-increment-2.md, line 29.** Find this exact line:

```text
directly ABOVE `    private static int FreeTestPort()`:
```

Replace with:

```text
directly ABOVE the indented line `private static int FreeTestPort()`:
```

**03-fix-increment-2.md, line 360.** Find this exact line:

```text
| M7 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace `        var inFlight = ExportsInFlightForTest;` with `        var inFlight = 0; // MUTANT M7: the in-flight arm no longer reports` | n/a — wrong value, not a deletion | `An_export_still_being_received_is_not_reported_as_no_export` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself | G1 after apply and after restore | driver observed exactly that before dispatch |
```

Replace with:

```text
| M7 | guard | `FakeOtlpCollector.cs`, in `AssertNoRequestAsync`: replace the indented line `var inFlight = ExportsInFlightForTest;` with `var inFlight = 0; // MUTANT M7: the in-flight arm no longer reports`, keeping its eight spaces of indentation | n/a — wrong value, not a deletion | `An_export_still_being_received_is_not_reported_as_no_export` | **RED** — `Assert.ThrowsAny() Failure: No exception was thrown`; that is the false green itself | G1 after apply and after restore | driver observed exactly that before dispatch |
```


## Increment 11 — commit and gates

```bash
git add docs/plans/672-fake-otlp-collector/
git diff --cached --name-only
git commit -m "docs(plans): keep indentation outside the runbooks' inline code spans"
```

Then run **G1** and **G2** on the whole solution (nothing should change: expect Api.IntegrationTests
**1669**) and paste the four `Test Run Successful.` lines. No mutation row: this increment changes no
executable behaviour.

## Report back

The four corrected lines, the G1 tail, the four `Test Run Successful.` lines, and the pushed head SHA.
