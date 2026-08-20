# Aspire AppHost design for issue #565

## Status

Approved by the user on 2026-08-20, including the coherent OTLP transport-profile precedence described below. During implementation-plan review, primary OpenTelemetry .NET 1.17 source narrowed the SDK mitigation from process-global environment clearing to unconditional exporter-property assignment; that safer correction is included in the combined design/plan signoff below. Runtime verification later exposed that a C# AppHost with no launch profile runs as Production under `aspire run`, so Aspire 13.5 writes but does not reload its generated PostgreSQL password from user-secrets; the source-backed Development launch-profile correction below is part of the same durable-volume contract. This document describes the local-development orchestration boundary only. It does not change production, simulation, Testcontainers, or the API's public HTTP surface.

## Goal

Add a .NET Aspire 13.5 AppHost that starts Cluckwork's existing development stack from the repository root:

- PostgreSQL 18.4 with durable local data;
- Redis 7.4, using the API's existing shared-state configuration path;
- the existing API in `Development` with its current migration and user-secrets behavior;
- the Vite SPA with a dynamically supplied API proxy target; and
- Aspire dashboard logs, traces, metrics, health, and resource status.

The new path must be additive. Existing Docker Compose, production boot, simulation, and integration-test startup behavior remain unchanged. Simulation files receive only the configuration-key bookkeeping required by the repository's boot-guard rule.

## Constraints and non-goals

- Use Aspire 13.5 packages and .NET 10.
- Pin PostgreSQL and Redis by exact tag and SHA-256 digest, using the same values already committed in Compose.
- Do not add the stock Aspire ServiceDefaults project or package Aspire client integrations into the API.
- Do not add or alter API health endpoints. Model the existing `/health/live` and `/health/ready` contracts.
- Do not commit credentials, connection strings, or generated Aspire state.
- Do not introduce provider-specific deployment configuration or an Aspire production publishing path.
- Preserve `Otlp:*` validation, redaction, production security checks, and one-shot degradation behavior.

## Options considered

### A. Minimal AppHost plus a small telemetry resolver — selected

Keep orchestration in one AppHost project and add one pure configuration-resolution seam to the existing telemetry setup. The AppHost references the existing API and Vite application directly. Tests exercise the resolver, resource model, immutable pins, and runtime behavior.

This is the narrowest design that meets the issue. It keeps the production application independent of Aspire and leaves operational entry points intact.

### B. Generic configuration-overlay abstraction

Build a reusable layer that overlays canonical application settings on OpenTelemetry environment variables and could later support more aliases or sources.

Rejected because the repository has one concrete compatibility problem, not a general configuration-merging domain. A generic overlay adds precedence machinery and a larger testing surface without an identified second consumer.

### C. ServiceDefaults/client-integration adoption

Add Aspire ServiceDefaults, change API telemetry and health registration to the generated conventions, and use Aspire PostgreSQL/Redis client integrations.

Rejected because it replaces working Cluckwork infrastructure, expands production coupling, and directly conflicts with issue scope. Aspire should orchestrate this application, not redefine it.

## Resource model

Create `src/Cluckwork.AppHost/Cluckwork.AppHost.csproj`, add it to `Cluckwork.sln`, and reference `Cluckwork.Api`. The project uses `Aspire.AppHost.Sdk/13.5.0` (which supplies `Aspire.Hosting.AppHost`) and explicitly versioned 13.5.0 hosting packages for JavaScript, PostgreSQL, and Redis. Its NuGet lock file is committed with the other project lock files.

Commit a deterministic root `aspire.config.json` that selects this AppHost so `aspire run` has no discovery prompt or generated path drift. Commit one C# AppHost launch profile under `src/Cluckwork.AppHost/Properties/launchSettings.json` that sets both `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` to `Development` without fixed URLs or telemetry endpoints. This makes the AppHost itself load its `UserSecretsId` store; setting only the API child environment does not do that. These files contain no secret or machine-specific value. Ignore only Aspire's local generated state, if the installed CLI creates any; do not ignore the rooted configuration or launch profile.

The AppHost declares these resources:

1. `postgres`: repository `postgres`, tag `18.4-trixie`, SHA-256 `3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a`. Apply the tag, configure the named data volume while Aspire can still read that reviewed tag and select PostgreSQL 18's `/var/lib/postgresql` layout, then apply the digest for the final immutable image identity. Add a database resource and inject it into the API under the connection-string name `Default`. Keep Aspire's implicit secret password parameter: locked 13.5 source already persists it to the AppHost user-secrets store in run mode. The Development launch profile is load-bearing because Aspire only loads that store into AppHost configuration in Development; without it, every run generates and overwrites a different password while the volume retains the old PostgreSQL role credential.
2. `redis`: repository `redis`, tag `7.4-alpine`, SHA-256 `e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2`. Redis is intentionally ephemeral. Inject its connection expression into `SharedState__Redis__ConnectionString`, which exercises Cluckwork's existing Redis-backed registrations rather than an Aspire client integration.
3. `api`: the existing API project, explicitly run with both .NET and ASP.NET Core environment set to `Development`. Because the API has no launch profile or Kestrel endpoint configuration, declare one dynamically allocated `.WithHttpEndpoint(name: "http")` before any endpoint reference or health annotation. The API references the database, receives the Redis setting, waits for PostgreSQL and Redis, and exposes both existing health checks in the resource model. Readiness is the dependency gate for downstream startup.
4. `web`: the existing `web/` Vite app. It waits for the API and receives `VITE_API_TARGET` from the API's Aspire HTTP endpoint expression. `AddViteApp` already declares the Vite HTTP endpoint; do not add a second `.WithHttpEndpoint()` to it.

Host ports remain dynamically allocated. The Vite configuration reads the process-level `PORT` value when present, validates it as a TCP port, and retains `5173` as the standalone-development fallback. When `PORT` is supplied, `server.strictPort` is true so Vite cannot silently move while Aspire advertises the assigned port. Its existing `VITE_API_TARGET` fallback to `http://localhost:8080` remains intact.

The PostgreSQL named volume is the only durable AppHost data resource. Normal stop/start preserves farm data and reuses the generated PostgreSQL password from the uncommitted AppHost user-secrets store. The developer documentation provides an explicit, targeted reset procedure for that volume rather than deleting it automatically.

## Startup and health flow

```text
PostgreSQL healthy ─┐
                    ├─> API starts in Development ─> /health/live
Redis healthy ──────┘                              └> /health/ready ─> Vite starts
```

The API retains ownership of migrations and health semantics. Development startup performs migrations exactly as it does today. Aspire observes those contracts and orders resources; it does not duplicate migration logic or invent a second readiness definition.

## Telemetry compatibility boundary

`AddCluckworkTelemetry` remains the only registration entry point. Extract its option construction into a small internal pure resolver that records which profile was selected, then pass the resolved `OtlpOptions` through the existing protocol and endpoint validation path. Keep resolution and Cluckwork validation inside the existing process-role exception boundary so serving processes fail closed while one-shot processes retain their current warning-and-disable behavior. Do not claim SDK header validation that the application does not perform.

Aspire supplies the standard OpenTelemetry variables `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, and `OTEL_EXPORTER_OTLP_HEADERS`. The resolver supports these variables only when no canonical Cluckwork transport key is present.

### Deliberate security clarification to the issue wording

The issue describes fallback when each corresponding `Otlp:*` setting is absent. Literal per-leaf fallback is unsafe: Aspire's standard header contains the dashboard collector API key. If an operator supplies only `Otlp:Endpoint` for another collector, per-leaf fallback would combine that endpoint with Aspire's header and send the dashboard credential to the other collector.

Therefore endpoint, protocol, and headers are a coherent transport profile:

- If any of `Otlp:Endpoint`, `Otlp:Protocol`, or `Otlp:Headers` is present in configuration—even if blank—the resolver uses only the canonical `Otlp:*` transport profile and ignores all three standard environment variables.
- Otherwise it maps all three standard variables as one profile.
- A present blank `Otlp:Endpoint` continues to disable exporting and cannot be re-enabled by ambient environment variables.
- `Otlp:AllowInsecureEndpoint` remains Cluckwork's explicit application security acknowledgement and applies to the selected profile.

This preserves `Otlp:*` authority without mixing collector credentials across configuration sources. It intentionally does not map signal-specific endpoints, service names, samplers, resource attributes, or other standard variables.

### SDK environment parsing

Resolving Cluckwork options is not sufficient on its own: OpenTelemetry .NET constructs each `OtlpExporterOptions` by reading process-level `OTEL_EXPORTER_OTLP_*` variables before Cluckwork's exporter callback runs. The locked 1.17 SDK ignores malformed endpoint/protocol values and its explicit property setters override parsed values, but Cluckwork's current callback assigns headers only when nonblank. A canonical endpoint with no canonical header can therefore retain Aspire's parsed dashboard API-key header and send it to the wrong collector.

Configure every signal's exporter endpoint, protocol, and headers unconditionally from the already resolved profile, using an explicit empty header value when the profile has no header. This uses the SDK's documented setter precedence and prevents it from retaining a collector credential. Do not mutate process-global environment variables: clearing them is unnecessary once all three transport setters are unconditional and would create ordering/race hazards for co-hosted test or tool scenarios. Timeout, compression, temporality, sampling, service identity, resource variables, and signal-specific variables remain untouched. (`AddOtlpExporter` does not support the signal-specific endpoint/protocol/header variables; supporting them remains out of scope.)

Exporter environment verification runs in isolated subprocesses so each case owns its inherited standard variables and can prove actual outbound requests without mutating the xUnit process. The ordinary integration factory still selects the canonical disabled profile so ambient developer or CI telemetry cannot turn unrelated tests into exporters. Tests assert actual outbound headers and payloads, not only the pure resolver result.

## Test isolation

As a mandatory implementation step, the integration application factory explicitly supplies a blank canonical `Otlp:Endpoint`, which selects the canonical disabled profile and prevents ambient developer or CI `OTEL_EXPORTER_OTLP_*` variables from turning ordinary tests into exporters. Focused pure-resolver tests construct isolated configuration rather than depending on process environment. Tests of process-environment neutralization run only in isolated subprocesses; they do not share xUnit collection-level process state with any `WebApplicationFactory`.

## Verification design

### Configuration tests

- Standard endpoint, protocol, and headers configure the current exporter when canonical transport keys are absent.
- Canonical `Otlp:*` wins as a complete profile when any canonical transport key is present.
- Endpoint-only, protocol-only, headers-only, and blank canonical keys exercise the complete precedence truth table.
- A blank canonical endpoint disables ambient standard telemetry.
- An explicit canonical endpoint never receives a common standard header, proven at the constructed exporter or outbound request.
- Malformed ignored ambient endpoint/protocol values remain harmless characterization cases, while the header-crossover case is the regression that must go red before the unconditional header setter.
- Invalid protocol, endpoint, and insecure-production combinations retain current serving failures.
- One-shot roles retain warning-and-disable behavior.
- Header values and collector credentials never appear in logs or exception messages.
- `ProcessRoleGuardTests.ServingOnlyGuards` contains one row per new standard-variable serving violation, and the simulation bootstrap, manifest, and harness verifier are updated together for the new configuration inputs.

### Resource-model and guard tests

- The AppHost project is in the solution and its package versions and lock file are fixed.
- The committed C# AppHost launch profile has exactly one project profile, sets both AppHost environment variables to `Development`, and carries no fixed URL, telemetry endpoint, credential, or generated value. Removing `DOTNET_ENVIRONMENT=Development` must make the structural guard fail.
- The model contains PostgreSQL, Redis, API, and Vite with the declared references, waits, environments, endpoints, and health contracts.
- The resolved container annotations contain the exact PostgreSQL and Redis repository and digest values above. Aspire 13.5 stores tag and SHA-256 mutually exclusively: `WithImageSHA256` clears the annotation's tag, so the final digest-pinned annotation deliberately has `Tag=null`. Independent full-reference/component assertions and a bounded structural source guard prove the exact tag input, digest input, and PostgreSQL tag→volume→SHA order instead of pretending the final annotation can carry both fields. The repository-wide guard also detects Aspire's split `WithImageTag`/`WithImageSHA256` representation instead of assuming the existing full-reference regex can see it.
- The resolved PostgreSQL volume annotation targets `/var/lib/postgresql`; a mutation that moves volume configuration before the tag must make this test fail.
- The repository-wide Redis pin guard follows the same discover-then-validate discipline as PostgreSQL and understands both full image references and Aspire's split representation.
- Mutation checks independently change each AppHost tag and digest and prove the corresponding guard fails.
- Vite configuration tests cover dynamic process `PORT`, strict binding, and the existing standalone fallback; the runtime smoke confirms Aspire's advertised endpoint serves the SPA.

### Runtime acceptance

Use a worktree-local Aspire CLI installation so verification does not alter the developer's global tools. From the repository root:

1. start the AppHost and confirm PostgreSQL, Redis, API, and Vite reach their expected healthy/running states;
2. load the SPA and confirm Aspire's advertised web endpoint serves it;
3. send a valid-shaped unsuccessful login request to `/api/v1/auth/login` on Aspire's advertised **web** endpoint, not directly to the API, proving the dynamically injected Vite proxy target while exercising both the Redis-backed IP limiter and an EF Core query without requiring a provisioned administrator; attach a freshly generated valid W3C `traceparent` value;
4. use bounded `aspire logs` queries to prove the API's Serilog console output exists for the request path, and query `aspire otel` by the generated trace ID to prove the corresponding spans/trace rather than accepting any recent trace; inspect metrics in the dashboard separately and verify no collector credential appears in logs;
5. inspect Redis for the corresponding namespaced limiter key, proving the API exercised Redis rather than merely receiving a connection string or observing a healthy sidecar;
6. confirm the generated PostgreSQL parameter key exists without reading its value, snapshot the user-secrets file modification time, create a uniquely named harmless database marker, stop and restart the AppHost, prove the secrets file was not rewritten and that exact marker survives;
7. resolve the exact volume from the running resource/container metadata, display and validate the target, remove only that volume through the documented reset, then prove the marker is absent after a clean start; and
8. rerun the existing .NET and frontend suites to prove legacy paths are unchanged.

The real stack smoke is manual/non-CI unless its implementation provides a unique run scope and guaranteed cleanup. Model tests stay process-only. Persistent volumes, AppHost secrets, and container-engine state are never assumed to be isolated merely because the CLI binary lives under the worktree.

## Documentation

Update the short contributor path and relevant development documentation with:

- the Aspire 13.5 CLI/container prerequisites;
- the root `aspire run` command;
- dashboard discovery and the dynamic endpoint behavior;
- the relationship to user secrets and Development migrations;
- persistence and targeted reset behavior; and
- the fact that Docker Compose remains the production-like path.

No product glossary or in-app Help update is required because this adds a developer workflow and no user-visible farm concept or behavior.

## Risks and controls

- **Credential crossover:** prevented by coherent telemetry profiles and an explicit non-leak test.
- **Mutable container images:** prevented by tag-plus-digest declarations, repository-wide guards, and mutation verification.
- **False-green orchestration tests:** controlled by runtime smoke tests against real PostgreSQL, Redis, API, Vite, and dashboard signals.
- **Ambient telemetry in tests:** prevented by an explicit canonical disabled profile in the application factory.
- **PostgreSQL 18 volume mismatch:** controlled by declaration order, the version-aware volume API, and restart persistence verification.
- **Drift from production behavior:** controlled by keeping Aspire confined to the AppHost and leaving existing production/simulation/Testcontainers entry points unchanged.
