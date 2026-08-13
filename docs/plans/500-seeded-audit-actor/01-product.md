# Product: seeded records name a real person (#500)

## Problem

Someone opening a demo or simulation farm sees, on five different screens
(Flocks, Egg grades, Daily entry history, Sales, Expenses):

> Created by **(unresolved)** on 2026-08-04

On the demo fixture that text appears on roughly 256 rows. It reads as a broken
product. The person demoing has to apologise for it, and the "who did this"
feature — the whole point of the History line — tells them nothing.

The simulation fixture has a second, sharper version of the same problem: it
exists specifically to look like a real farm with a real staff of managers,
sales people and workers. Every record in it currently claims nobody made it, so
any question the fixture exists to answer ("who's entering the eggs?", "which
sales person books the most orders?") has no answer in the data.

## Success metric

**Zero** records in a freshly seeded farm show an unnamed author.

Measured two ways, both automated:

1. After `seed --profile demo` and after `seed --profile simulation`, no audit
   event carries the placeholder author — currently 256 of them do on demo.
2. On the simulation fixture, the author is *plausible*: daily entries are
   authored by farm workers, sales orders by sales staff — not all 256 rows by
   one person. Target: every persona the fixture creates for a job appears as the
   author of at least one record of that job's kind.

## Announcement — the blog post before the feature

**Demo and simulation farms now show who did what.**

Every record in Cluckwork carries a short history line — who created it, and who
last changed it. Until now, farms created from our demo and simulation fixtures
left that line blank, showing a placeholder instead of a name. From this
release, seeded data is attributed the way real data is: the demo farm's records
are signed by its administrator, and the simulation farm's records are signed by
the member of staff who would really have made them — workers log the day's eggs,
sales staff book the orders. Nothing changes for records you created yourself;
they were always attributed correctly. Farms created from now on can no longer
file a record with no author at all — the system refuses. Farms seeded before
this release keep the placeholder until they are seeded afresh.

## Screens

No new screens and no new UI code. The change is the **text** rendered by the
existing #494 History line on Flocks, Egg grades, Daily entry history, Sales and
Expenses — from `Created by (unresolved)` to a person's email. No mockups.

## Product decision — DECIDED 2026-08-11: option A

The owner chose **A**: `seed --profile demo` gains the same Owner-user preflight
the simulation profile already has, fails with the same "run `bootstrap-admin`
first" message when there is none, and signs every demo record with that admin.
The three options and their costs are kept below as the record of why.

**Re-confirmed 2026-08-11**, after a review round found option A's cost is
larger than this doc originally stated: "demo seeds against a bare database" is
not merely a habit, it is a contract pinned by `SeedCommandTests` with a comment
asserting that `seed --profile demo` *"needs nothing but a connection string"*.
Three tests change. The owner accepted that and rejected two alternatives that
would have preserved the contract — both are recorded in `03-program-design.md`
with the reason each was worse.

**A third consequence, found only when Gate 1 was finally reviewed** (see
`04a-review-gates-1-2-4.md`): `bootstrap-admin` prints its generated password
**only on first provisioning** — a re-run against an already-provisioned account
prints "already provisioned … nothing to do" and names nobody. On a persistent
dev database, a reused CI container or a shared demo host, demo data is
therefore attributed to an Owner **whose credentials nobody has**, and viewing
the demo requires logging in as somebody. The escape is the documented
`recover-admin` break-glass procedure, which resets that Owner's password. This
does not change decision A, but it belongs in the docs the PR updates, because
it is the situation a returning developer actually hits.

---

`seed --profile demo` can legitimately run against a database that has **no
users at all** — unlike the simulation profile, it does not require
`bootstrap-admin` to have been run first, and it creates no users itself. So
"attribute demo data to the Owner" has a case where there is no Owner. Three
answers, in preference order:

- **A. Demo requires an admin, same as simulation.** Demo gains the same
  preflight the simulation profile already has, and fails with the same clear
  "run `bootstrap-admin` first" message. Records are signed by that admin.
  Matches the documented setup order in `README.md`. Cost: a demo seed that
  works today against a bare migrated database starts failing until you run one
  extra documented command.
- **B. Demo signs records with the admin if there is one, otherwise a built-in
  demo persona the seeder creates.** Never fails, always names somebody. Cost:
  the seeder starts creating a user, and that persona needs a password nobody
  ever uses, or a disabled account.
- **C. Demo signs records with a non-person "system" author.** Never fails, no
  new user. Cost: the History line reads `Created by system` on 256 rows, which
  is honest but still not a person — it only half-solves the problem the issue
  reported.

Recommendation: **A** — it is the smallest change, it matches what simulation
already does and what the README already tells people to do, and it is the only
one of the three where the demo farm looks exactly like a real farm.
