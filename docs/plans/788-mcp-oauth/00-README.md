# MCP authentication — OAuth 2.1 via OpenIddict (#788)

**Status: design only. No code has been written and nothing here has shipped.**

#788 began as a question: MCP clients expect OAuth discovery and a refreshable token, and Cluckwork's
refresh is an HttpOnly cookie, so nobody could actually connect an assistant. This directory is the
design that answered it. Read it as *what was intended at the time*, per [`../README.md`](../README.md)
— if it ever disagrees with shipped code, the code is right.

| File | What it is |
|---|---|
| [`01-decision.md`](01-decision.md) | The decision, the rejected alternative in full, and the accepted risks. |
| [`02-design.md`](02-design.md) | How it works: flow, consent, scopes, revocation, and the checks that must be re-established. |
| [`03-libraries.md`](03-libraries.md) | Every package evaluated, with licence, maintenance and verdict — so nobody re-researches it. |
| [`04-verified-findings.md`](04-verified-findings.md) | Facts checked against source and the restored assemblies, including four that overturned earlier assumptions. |

## Why this is not in `docs/decisions/`

A decision record here is defined as *"the relocated rationale for a rule that also appears — in one
compressed paragraph — in `AGENTS.md`"*. Nothing has shipped, so there is no rule to compress; filing
there would mean writing an `AGENTS.md` bullet for code that does not exist.

**A decision record is owed when the implementation lands**, and the rule it should carry is already
identifiable:

> An OAuth-authenticated request does not pass through `CredentialEpochMiddleware`, so every
> fail-closed check that middleware performs — disabled user, suspended farm, must-change-password —
> must be re-established on the OAuth path. None is inherited.

That belongs in `AGENTS.md` with a `→` link here, written by whoever completes [#796](https://github.com/mforce/cluckwork/issues/796).

## Slices

| Slice | What | Depends on |
|---|---|---|
| [#795](https://github.com/mforce/cluckwork/issues/795) | Stand up OpenIddict — tables, migration, token issuance | [#794](https://github.com/mforce/cluckwork/issues/794) |
| [#796](https://github.com/mforce/cluckwork/issues/796) | Fail-closed checks on the OAuth path | #795 |
| [#797](https://github.com/mforce/cluckwork/issues/797) | Dynamic client registration | #795 |
| [#798](https://github.com/mforce/cluckwork/issues/798) | Consent screen + open-redirect guard | #795, #797 |
| [#799](https://github.com/mforce/cluckwork/issues/799) | Connected-apps screens | #795, #798 |
| [#800](https://github.com/mforce/cluckwork/issues/800) | Audit provenance columns | #795 |

Slices 1-3 are invisible to users. That is deliberate: the risk in this feature is in the plumbing,
not the screens, so it lands first where it is cheapest to fix.
