# Keep farm configuration and identity Owner-only (#729)

> **Rule.** The one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).
> This file records why the authorization boundary differs from the broader
> `AdminOnly` operations tier.

**Status:** accepted
**Date:** 2026-09-14

## What happened

No incident occurred. Issue #727 exposed a mismatch between the product role
table and the authorization policy. The product specification limits Managers
to farm operations, inventory, health, and reports, but the API and navigation
also let them change farm configuration and identity.

## The rule

Only an Owner may open or update Farm settings or change the farm logo or
banner. Every authenticated role may still read `/account`, `/account/logo`,
and `/account/banner` because the SPA uses those reads to format screens and
render the farm identity.

## Why not keep the `AdminOnly` policy

`AdminOnly` is the historic name for the Owner and Manager operations tier.
Using it for configuration made a Manager able to change the currency,
timezone, locale, unit system, sale-allocation policy, palette, logo, and
banner. Those values affect the whole farm and are outside the Manager purpose
in product specification section 5.1.

## What this does not cover

The other `AdminOnly` routes remain unchanged. Managers still perform farm
operations, inventory work, corrections, and reporting. The role-agnostic
account and image reads also remain unchanged.

## How it is enforced

- `FarmSettingsTests.Manager_CannotReadOrEditSettings` checks both settings
  routes and `Manager_LoadsTheFarmBrandFromTheRoleAgnosticAccountRead` protects
  the shell contract.
- `FarmLogoTests.NonOwners_CannotChangeTheBranding` and
  `FarmBannerTests.NonOwners_CannotChangeTheBanner` check the image mutations.
- `nav.test.ts` checks that the Owner sees Farm settings and the Manager does
  not.
- `manager.spec.ts` drives the built SPA as a Manager, keeps the operational
  Setup destinations visible, and checks that Farm settings and Users are
  absent.
- The in-app Help guide and `specs/product/GLOSSARY.md` name the Owner-only
  edit boundary while preserving the role-agnostic formatting and branding
  reads.

Nothing checks the wording in product specification section 5.1 or
`AuthPolicies.cs`; that wording relies on review.
