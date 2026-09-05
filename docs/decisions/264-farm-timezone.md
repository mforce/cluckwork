# Farm timezone, and the tzdata/ICU image constraint (#264)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted · **Date:** 2026-07

## The rule

The default account carries a fixed `"UTC"` migration literal (since
[#283](283-migrations-base-provisioning.md)). A real farm sets its actual IANA
zone via **Settings → timezone** after first login (`Account.UpdateSettings`).

**Amended 2026-09 (#603).** `provision-account` now also accepts an optional
`--timezone`, so an operator who already knows the farm's zone can commit it
with the farm instead of leaving its first day of data recorded against `UTC`.
Omitted, the behaviour is exactly as before. This does **not** reinstate the
`Seed:TimeZoneId` config lever retired with #283's runtime seeder, and that
distinction is the point: a *config key* read at boot applied silently to
whatever the process was doing, while a *CLI argument* is stated per farm by
the operator creating it, and is validated at that boundary with
`FarmSettingsRules.IsKnownTimeZone` — the same predicate
`UpdateFarmSettingsValidator` uses, so a zone the CLI accepts is always one
Settings would accept. An unresolvable zone fails the command before any write
rather than committing a farm whose dates cannot render.

## Why the image constraint follows

The farm clock resolves IANA zones via `TimeZoneInfo.FindSystemTimeZoneById` for
every safety-sensitive local-day decision, and **fails closed**. So the runtime
**image MUST carry tzdata + ICU**:

- the Ubuntu 24.04 (Noble) `aspnet:10.0` base does;
- **never** an Alpine/chiseled base without tzdata;
- **never** `InvariantGlobalization=true`.

## How it is enforced

`AddCluckworkPersistence` asserts a representative IANA canary resolves at boot
(`TimeZoneAvailability.EnsureResolvable`), for **both** process roles — a bad
image fails the boot loudly instead of surfacing later as a per-request
`FarmTimeZoneException`. It takes no `ProcessRole` parameter on purpose: the
constraint applies to serving processes and one-shot verbs alike, and
unconditional code says so better than a parameter nobody branches on
([#347](347-process-role.md)).
