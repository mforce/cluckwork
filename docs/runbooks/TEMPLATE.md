# Runbook: <what this recovers or performs>

**When to use this:** the symptom, precisely.

**Not this runbook:** the neighbouring situation that looks identical and needs a
different procedure — link it.

**Blast radius:** who or what is affected while this runs.

**Prerequisites:** access, tooling, and any approval needed *before* starting.

**Last drilled:** YYYY-MM-DD by <who>, against <which environment> — or `not
recorded`.

---

## Procedure

### 1. Confirm you are in the right situation

The check that distinguishes this from the look-alike named above. Do this
first — a recovery procedure applied to the wrong fault usually makes it worse.

```bash
<command>
```

Expected:

```
<output>
```

### 2. <Step>

```bash
<command>
```

> **Destructive.** This <what it destroys>. To undo: <how>, or **this cannot be
> undone** — say which, plainly.

## Verify

Not "it did not error" — the positive check that the thing now works.

## If it fails

The two or three ways this goes wrong, and what to do about each. If the answer
is "escalate", name who and how to reach them.

## Drill

Steps to prove the procedure still works, safe against a scratch environment.
Run this after any change to the code paths above and update **Last drilled**.

1. …
2. …
3. Expected end state: …
