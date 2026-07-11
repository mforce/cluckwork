# Cluckwork

Poultry farm management — starting with egg-producing layer operations, with architectural headroom for broilers, pullets, breeders, live bird sales, meat products, and hatchery modules.

## What it does

Cluckwork helps a poultry farm answer its daily operational questions from a single system:

- How many eggs were produced today, and by which flock/house?
- Which egg lots are available by grade, date, flock, location, and restriction status?
- Are any eggs restricted due to medication withdrawal?
- How much feed and water were consumed?
- What is the flock's hen-day production rate?
- What is the current live bird count?
- What was sold, to whom, and from which egg lots?
- What is the farm's sales, expense, and profitability picture?

## Architecture

The data model starts from a multi-tenant account root so the system scales past a single farm:

```
Account / Tenant
  Users
  Farms (localized: timezone, locale, currency)
    Houses (cage, deep litter, free range, aviary...)
      Flocks (any species/production purpose — not hardcoded to layers)
```

Flock classification is extensible: `species` (chicken, duck, quail, turkey...), `production_purpose` (layer, broiler, pullet, breeder, dual-purpose...), and `production_model` (egg, meat, raising, breeding, mixed).

## Phase 1 scope (walking skeleton)

Phase 1 ships the core egg-production loop:

- **Daily Entry** — submit egg production by grade, feed consumption, water, mortality, and culls
- **Egg Lots & Inventory** — egg lots tracked from production through sale with full traceability back to flock/house/date
- **Medication Withdrawal** — medication records automatically restrict affected egg lots from sale
- **Product Catalog & Sales** — product-generic sales model; egg products map to grades, allocate from lots via FIFO, and generate sale inventory movements
- **Bird Ledger** — point-in-time bird inventory reconstructed from placement, mortality, and cull movements
- **KPIs & Alerts** — hen-day production, saleable egg %, feed cost per dozen, flock alerts
- **Multi-farm localization** — each farm has its own timezone, locale, and currency; sales orders snapshot the farm's currency at creation

Phase 1 does **not** include broiler optimization, meat processing, hatchery, feed formulation, payroll, accounting, offline mobile, or IoT automation. The schema reserves paths for all of these.

## Stack

- **Backend:** C# / .NET (ASP.NET Core Web API)
- **Database:** PostgreSQL
- **Frontend:** React (TypeScript)

## Specs

The canonical product and technical specification is [`specs/product/specs.md`](specs/product/specs.md).

It covers: data model schemas, business rules, use cases, wireframe coverage, transaction boundaries, idempotency rules, data storage conventions, KPI formulas, and module phasing through Phase 5.

## Repo layout

```
cluckwork/
  specs/product/     — product & technical spec, wireframes, CHANGELOG
  src/               — .NET solution (API, domain, data access)
  tests/             — integration and unit tests
  deploy/            — Docker Compose, Traefik reverse proxy config
```
