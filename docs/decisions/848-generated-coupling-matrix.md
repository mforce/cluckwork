# Generate the coupling matrix from live evidence (#848)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).

**Status:** accepted
**Date:** 2026-09-14

## What happened

No incident. Design section 3.4 lived only in the #514 epic comments. Seven observable `W`/`R`/dash cells differ from the generated matrix, including two letter classifications. The record also preserves the historical `E`, `Q`, and Platform cells separately because the walks cannot observe them. This record supersedes that copy with a generated file.

## The rule

Generate `tests/Cluckwork.Application.Tests/Architecture/Data/coupling-matrix.md` from `module-ledger.json`, the module-edge walk, the EF-model table walk, and the adapter-reach walk. Do not edit the generated Markdown. The real-tree test rejects any invalid source report before rendering, compares normalized line endings with a fresh render, and checks that live module pairs equal the ledger's edge pairs.

## Why not the obvious alternative

Keeping the matrix as prose asks every later change to update two copies of the same dependency information. The ledger and walks already inspect the source and model, so a second hand-maintained table would drift without a distinct source of truth.

## What this does NOT cover

`W` means a ledgered synchronous write and `R` means a ledgered read or validation edge. `P` marks the free Platform hub, and `A` counts Platform adapter reaches. The observable table compares only `W`, `R`, and dash values. The second table preserves the hand-written `E`, `Q`, and Platform cells against the generator's structural output. The matrix does not authorise a dependency, schema change, or data write.

## How it is enforced

`CouplingMatrixRealTreeTests.RealTree_CommittedMatrixMatchesRegeneration` renders and compares the committed file. To regenerate it after a reviewed ledger or walk change, run:

```bash
CLUCKWORK_REGENERATE_MATRIX=1 dotnet test tests/Cluckwork.Application.Tests --filter FullyQualifiedName~CouplingMatrixRealTreeTests
```
