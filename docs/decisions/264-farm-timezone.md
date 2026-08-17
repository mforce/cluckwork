# Farm timezone, and the tzdata/ICU image constraint (#264)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted · **Date:** 2026-07

## The rule

The default account carries a fixed `"UTC"` migration literal (since
[#283](283-migrations-base-provisioning.md)). A real farm sets its actual IANA
zone via **Settings → timezone** after first login (`Account.UpdateSettings`),
**not** at provisioning time — the `Seed:TimeZoneId` config lever from before
#283 is retired along with the runtime seeder it fed.

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
