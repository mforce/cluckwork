# <Short imperative title> (#<issue>)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted · superseded by #… · reversed
**Date:** YYYY-MM-DD

## What happened

The incident, concretely. What broke, how it surfaced, how long it was live, and
why nothing caught it. Be specific enough that a reader who was not there can
tell whether their situation is the same one.

If there was no incident — this is a forward-looking choice — say so plainly. A
rule with no incident behind it is weaker, and a reader is entitled to know that
before treating it as load-bearing.

## The rule

One paragraph, imperative. This is the text that gets compressed into
`AGENTS.md`. State the consequence of breaking it in the same breath.

## Why not the obvious alternative

The approach a reasonable person reaches for first, and the specific reason it
does not work here. This section is what stops the rule being "simplified" back
into the defect.

## What this does NOT cover

State the boundary at exactly the strength the argument supports. A rule
overstated is a rule that gets discovered to be false and then discarded whole.

## How it is enforced

The test, gate, or hook that fails when someone breaks this — by name and path.
If nothing enforces it, say **"nothing enforces this; it relies on review"**. An
unenforced invariant stated as a fact is a bug waiting to happen.
