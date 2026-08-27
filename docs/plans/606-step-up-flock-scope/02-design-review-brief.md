# Design review brief — #606

Review `01-threat-model.md` as a pre-implementation security design. This is
not code review and no files may be changed.

Contract sources, in precedence order:

1. GitHub issue #606 and repository `AGENTS.md` / `SECURITY.md`.
2. Shipped source at `f767dce0073aea17a7e4e8cd644224023d325c89`.
3. The draft design.

Adversarial review. Find what is wrong with this artifact. Assume the author is
overconfident. Look for unstated assumptions, missed entry points, hidden
coupling, false-green tests, stale async continuations, tenant/audit/concurrency
regressions, a request-selectable bypass, violations of repository conventions,
and failures under unexpected input. Do not validate or summarize.

For each finding output:

- `MERGE-BLOCKING` or `FOLLOW-UP`
- the exact claim/invariant affected
- source evidence with file and symbol
- the smallest corrective design change
- the mutation or test that would distinguish the corrected design from the
  current draft

Do not inflate severity to be safe. If no issue remains after thorough source
inspection, return `CLEAN` and list the files/symbols actually checked.

Role-specific lens is supplied in the invocation:

- **Architect:** module/seam fit, completeness, handler-first enforcement,
  idempotency and trusted caller shape.
- **Contrarian:** refute the load-bearing assumptions using pre-mortem,
  inversion, second-order effects, and mirrored-path attacks.
- **Repository/test specialist:** mechanically refute caller, locale, Help,
  simulation, k6, Playwright, and mutation-test inventories.

Send the final verdict through agmsg as well as stdout:

```bash
bash /home/mforce/.agents/skills/agmsg/scripts/send.sh \
  cluckwork-606 <your-agent-name> cw606-driver "<concise verdict and findings>"
```
