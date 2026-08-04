# Writing a guard (a test that asserts an invariant)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).


A guard is a test whose job is to *fail* when someone later does the wrong
thing — the migration freeze (`MigrationSecurityReviewTests`), the body-reading
endpoint check (`BodyReadingEndpointTests`), the simulation manifest's exact
counts. A wrong guard is worse than no guard, because it reads as safety. #407
spent **five** review rounds on one of them — four codex findings and one CI
failure, every one against the previous fix — and these are the rules that
would have collapsed it:

- **Run a local adversarial pass before the first push.** The *pass* is the
  rule; the tool is not. `codex-cli` is the fastest way to get one where it
  happens to be on PATH (`codex exec --sandbox read-only "…" < /dev/null` — the
  redirect is required, it hangs without it), but it is a maintainer-machine
  tool, **not a repo dependency**: nothing here installs it, CI does not use it,
  and a sandboxed agent environment generally will not have it. Check
  `command -v codex` rather than assuming. Without it, get the same pass another
  way — a second agent handed the diff and told to *refute* it, or simply
  running the mutation checks below before the first push instead of after the
  first finding. What matters is that something hostile reads the guard while
  the change is still local. The per-PR review fleet is written around "once the
  PR is up", which is right for a feature diff; for guard-shaped work it turns
  every iteration into a push and a multi-minute wait. Four of #407's five
  rounds were findable locally in seconds.
- **Mutation first, claim second.** Never write "this catches X" — in a
  comment, a commit message or a PR reply — before running the mutation that
  makes the guard go red. Three of #407's five findings were a comment
  asserting coverage the code did not have; the worst was an `IAnnotation`
  branch that was **unreachable dead code** sitting directly under a comment
  saying annotations were covered. If a branch exists to handle something,
  prove it executes.
- **Two misses of the same shape mean the METHOD is wrong.** Do not extend the
  list a second time. #407's fingerprint hand-enumerated "the properties that
  matter", missed one, gained one, missed another — and only held once it
  walked *every* property so an item is included because it **exists**, not
  because someone remembered it. Prefer "walk everything, exclude
  deliberately" over "list what I thought of" anywhere the domain can grow.
- **For a pinned/golden value, prove portability — repetition is not evidence.**
  Running a digest three times on one machine tests non-determinism *within* a
  process and cannot, even in principle, detect environment leakage, which is
  perfectly stable per machine. #407's digest embedded an absolute path to
  `System.Private.CoreLib.dll` and passed locally while failing CI. Assert the
  generated **content** carries no environment (no absolute paths, no
  `AppContext.BaseDirectory`, no assembly file names) plus a size ceiling if the
  generator recurses — see
  `InitialCreate_Description_ContainsNoEnvironmentSpecificData`. That fails by
  name on the next leak instead of surfacing as a mystery digest mismatch, and
  it stops the tempting "fix" of re-baselining the constant until CI agrees,
  after which the guard checks nothing.
- **Prefer the boring guard.** Complexity costs double here, because the thing
  being complicated is the thing you are trusting.
