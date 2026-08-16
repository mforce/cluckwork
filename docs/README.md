# Documentation map

Four kinds of document, four different questions.

| Directory | Answers | Audience |
|---|---|---|
| [`runbooks/`](runbooks/) | *How do I operate it under pressure?* | Whoever is on the host at 03:00 |
| [`decisions/`](decisions/) | *Why is it shaped this way?* | Anyone about to "simplify" a rule |
| [`schema/`](schema/) | *What is actually in the database?* | Generated — never hand-edited (#417) |
| [`releasing.md`](releasing.md) | *How do I cut and deploy a release?* | Maintainer |

Elsewhere in the repo:

| Where | What |
|---|---|
| [`../README.md`](../README.md) | What Cluckwork is, and running it |
| [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | Local development, tests, branches, commits |
| [`../AGENTS.md`](../AGENTS.md) | The canonical rule set — every invariant, for humans and coding agents |
| [`../SECURITY.md`](../SECURITY.md) | Reporting a vulnerability; what CI enforces |
| [`../specs/product/`](../specs/product/) | Product & technical spec, phase plan, [glossary](../specs/product/GLOSSARY.md) |
| [`../deploy/README.md`](../deploy/README.md) | Compose topology, caching, rollout ordering |
| [`../web/README.md`](../web/README.md) | SPA development |
| [`../tools/simulation/README.md`](../tools/simulation/README.md) | Load, E2E and simulation harnesses |

Also here: [`security/`](security/) (the log-redaction policy) and [`plans/`](plans/)
(per-feature planning records, kept for provenance — they are **not** current
documentation; a plan describes what was intended at the time, not what shipped).

## Which file does a rule go in?

A rule lives in **one** place, compressed, and links to the rest:

- the **rule** and the consequence of breaking it → `AGENTS.md`;
- the **narrative that earned it** — what shipped, which review round found it,
  what the wrong fix was → a record in `decisions/`;
- the **procedure** a human follows → a `runbooks/` entry with a drill;
- the **command** a newcomer needs → `README.md` or `CONTRIBUTING.md`.

Copying a paragraph into a second file creates two copies that drift, and drift
here has already produced contradicting statements of the same guarantee. Link
instead.
