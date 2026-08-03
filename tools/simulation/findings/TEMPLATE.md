# #243 Release-Rehearsal Sim Harness — Capacity Baseline Findings

**Run:** `{{RUN_ID}}` · generated `{{GENERATED_AT_UTC}}`

> ## This is an uncapped, co-located, non-sizing shakeout.
>
> The numbers below come from a k6 protocol-level load test against a
> throwaway `cluckwork-sim` compose stack — a Production-config app
> container **sharing this developer box's CPU/RAM with everything else
> running on it**, talking to a **local Postgres sidecar** over an
> unauthenticated loopback link, hit from **one source IP**, with every
> per-IP rate limiter raised to a number that will never trip. It is **not**
> a capacity plan, **not** a production SLA, and **not** a sizing exercise
> for any specific container shape. Read it as: "does the harness work,
> does the request mix behave sanely relative to itself, does anything look
> structurally wrong" — nothing more. See "Could NOT measure" below for the
> explicit list of questions this run cannot answer.

---

## 1. Header

| Field | Value |
| --- | --- |
| Commit | `{{COMMIT_SHA}}` (`{{COMMIT_SHA_SHORT}}`, branch `{{GIT_BRANCH}}`) |
| Seed | `{{SEED}}` |
| History depth | `{{HISTORY_DAYS}}` days |
| Reps | `{{REPS}}` (each: fresh `reset.sh` stack + seed) |
{{DIRTY_NOTE}}

### Manifest row counts (`tools/simulation/out/manifest.json`, captured per rep)

{{MANIFEST_ROW_COUNTS_TABLE}}

### Lifecycle-state matrix

{{LIFECYCLE_STATE_MATRIX_TABLE}}

### Run parameters

{{PARAMS_TABLE}}

### Compression factor / wall-clock

{{COMPRESSION_FACTOR_LINE}}

{{WALL_CLOCK_TABLE}}

### Persona coverage (the #243 Task 9 MUST-FIX this orchestrator verifies every run)

Capacity is `CAPACITY_VUS` VUs, each **its own** cast user, sized to the full
10-person cast (1 Owner / 1 Manager / 1 Sales / 3 Worker / 4 ReadOnly) —
`baseline.js`'s inter-phase drain gap (`capacity.startTime` pushed past
warmup's entire window) is what makes this collision-free; see that file's
header for the live-probed mechanics. Below: which of the 5 personas actually
produced capacity-phase requests, per rep.

{{PERSONA_COVERAGE_TABLE}}
{{PERSONA_COVERAGE_WARNING}}

### Cast-user coverage (PR #279: per-user, stricter than per-role above)

Role-level coverage above can pass (all 5 roles present) while still
missing an individual cast user, or — the exact collision the drain gap
exists to prevent — silently double-counting one user under two concurrent
VUs. `CAPACITY_VUS` must equal the cast size exactly (`baseline.js`'s
`setup()` hard-errors otherwise); given that, each of the `castSize` cast
users should be owned by **exactly one** capacity VU, every rep.

{{CAST_USER_COVERAGE_TABLE}}
{{CAST_USER_COVERAGE_WARNING}}

### Full deviation list (Production config, sanctioned deviations only)

Every consumer of this harness's output — this doc included — must carry
this list forward rather than presenting sim-stack numbers as
production-equivalent. All isolated to `tools/simulation/.env.sim`; see
`tools/simulation/README.md` for the load-bearing parameter table.

1. **All three per-IP rate-limit buckets raised** (Login, Refresh,
   ClientErrors) to 1,000,000. **Invalidates:** any claim about real-world
   per-IP throttling behavior under load.
2. **k6 manages the `cluckwork_rt` refresh cookie manually** — it is
   `Secure` and the sim stack is plain HTTP over loopback, so k6's cookie
   jar never resends it; the harness extracts and re-sends it itself.
   **Invalidates:** nothing measurement-wise; noted because it means this
   run does not exercise a browser's own cookie handling.
3. **A local Postgres sidecar** — plaintext connection, uncapped host
   resources, no managed-PG connection limits or TLS, co-located with the
   app on the same box as everything else this developer is running.
   **Invalidates:** any absolute throughput/latency number as representative
   of a networked, TLS, resource-capped production database; also means
   this run has ~zero transient network/connection faults to observe.
4. **A local OTLP sink** (`Otlp__Endpoint` → the sim-only `otel-collector`
   service) — never a developer's real `deploy/.env` collector endpoint.
   **Invalidates:** nothing — additive observability only.
5. **Single source IP** — every request in every rep originates from the
   one box running k6. **Invalidates:** anything about geographic/network
   distribution of load, and is part of why the rate-limit buckets above had
   to be raised.
6. **`Database__MigrateOnStartup=true`** — the sim stack migrates on boot
   instead of via a separate release step. **Invalidates:** anything about
   migration-time behavior under a separate/gated migration step.
7. **No traefik / TLS front door**, a placeholder `AllowedHosts`
   (`cluckwork-sim.local` — concrete, never `*`: #319 fails a Production
   boot on a wildcard, and this stack runs Production config), loopback-only
   port publish. Reachable on `127.0.0.1` because loopback is force-added to
   the host-filter list. **Invalidates:** anything about TLS-termination
   overhead or traefik routing.

---

## 2. Results — relative/shape data only

**No absolute throughput or latency figure below should be read as "this is
what the app can do."** Everything here is presented as a *distribution*
across reps (median + observed range) and as *relative* comparisons
(persona vs. persona, flow vs. flow) — never a single lifted number.

### 2.1 Capacity-phase latency shape (`phase:capacity` only; warmup discarded)

{{OVERALL_LATENCY_TABLE}}

**±10% p95 variance across reps is an OBSERVATION, not a pass/fail gate** —
this box is a noisy, co-located, uncapped shared machine; rep-to-rep spread
reflects host noise as much as (or more than) the app itself.

{{P95_VARIANCE_OBSERVATION}}

### 2.2 By persona (relative comparison, not absolute)

{{BY_PERSONA_TABLE}}

### 2.3 By flow (relative comparison, not absolute)

{{BY_FLOW_TABLE}}

### 2.4 Request-rate mix

{{REQUEST_RATE_TABLE}}

### 2.5 Correctness signals (status codes / checks / unexpected_status)

Every rep's data is published below **even if flagged** — a flagged rep is
never silently dropped from this report.

{{STATUS_CHECKS_TABLE}}

{{FLAGGED_REPS_NOTE}}

### 2.6 Resource utilization trend (`docker stats`, app/db/otel-collector)

{{RESOURCE_UTIL_TABLE}}

### Browser experience (#386 canary)

What a real browser saw while the above was happening. k6 measures the server's
answer; this measures the farm's screen — and the two only mean something
together, which is why they share a document.

{{BROWSER_VITALS_TABLE}}

### 2.7 Database growth (`pg_database_size`, before/after each rep's capacity phase)

`pg_stat_statements` is reset once per rep, **right before `k6 run` starts**
(i.e. before warmup too) — so the `pg-snapshot end` dump captures **both**
warmup and capacity queries together, unlike the k6 percentiles above, which
are filtered to `phase:capacity` only via k6's own tagging. Treat the
`pg-snapshot` files under each rep's raw output directory as
warmup+capacity combined; treat every k6-derived number in this doc as
capacity-only. The two are not directly comparable row-for-row.

{{DB_GROWTH_TABLE}}

### 2.8 Raw per-rep artifacts

{{REP_ARTIFACTS_NOTE}}

---

## 3. Could NOT measure

This run is an **uncapped, co-located, non-sizing shakeout** (see banner
above). It cannot — and does not attempt to — answer any of the following.
Each needs a deferred, capped/networked/TLS/prod-shape run this plan
explicitly scopes out:

- **Absolute production throughput or latency.** The app container here
  shares this developer box's full CPU/RAM with everything else running on
  it, has no CPU/memory cap, and is not the single-vCPU/1&nbsp;GB container
  shape the real deployment uses. Any req/s or ms figure in this doc is only
  meaningful relative to this run's *other* numbers, never as a standalone
  production capacity claim.
- **Whether #269's DB-retry work is needed.** The local Postgres sidecar is
  plaintext, unauthenticated, and co-located on the same loopback interface
  as the app — it has approximately zero transient network faults,
  connection resets, or TLS handshake failures to trigger a retry path
  against. A networked, TLS, managed-PG target is required to say anything
  about #269.
- **Npgsql connection-pool sizing versus production.** The pool here talks
  to an uncapped local Postgres with no `max_connections` pressure from any
  other tenant; production's managed database has different limits,
  latency, and neighboring load. Pool-queue behavior observed here does not
  transfer.
- **#273 telemetry volume at production scale.** The local OTLP collector
  receives exactly this run's traffic, from one IP, at this run's VU count —
  it says nothing about the metrics/traces volume a real multi-tenant,
  multi-IP production workload would generate.

---

*Generated by `tools/simulation/run-baseline.sh` from
`tools/simulation/findings/TEMPLATE.md`. This is an uncapped, co-located,
non-sizing shakeout — see the banner at the top of this document.*
