# Aspire AppHost Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an Aspire 13.5 AppHost that runs Cluckwork's PostgreSQL, Redis, API, and Vite development stack with durable database state, exact image pins, existing health contracts, and dashboard observability.

**Architecture:** Aspire remains a local orchestration shell around the existing applications; the API does not acquire ServiceDefaults or Aspire client packages. A small resolver gives canonical `Otlp:*` settings coherent-profile precedence over standard OTLP transport variables, and unconditional exporter setters prevent the OpenTelemetry SDK's constructor-parsed header from crossing collector profiles without mutating process-global state. Aspire model tests and repository-wide guards prove the resource graph, image identities, and PostgreSQL 18 mount.

**Tech Stack:** .NET 10, Aspire 13.5, xUnit, OpenTelemetry .NET 1.17, React 19, Vite 8, PostgreSQL 18.4, Redis 7.4.

**Spec:** `docs/superpowers/specs/2026-08-19-aspire-apphost-design.md`

## Global Constraints

- Work only in `/tmp/cluckwork-worktrees/issue-565-aspire-apphost` on `feat/aspire-apphost`.
- Do not commit, push, open a PR, or merge unless the user separately authorizes that action.
- Use `Aspire.AppHost.Sdk/13.5.0` and 13.5.0 for every explicit Aspire package.
- PostgreSQL is `postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a`.
- Redis is `redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2`.
- Keep PostgreSQL durable and Redis ephemeral.
- Do not add ServiceDefaults, Aspire client integrations to the API, new API endpoints, or production publishing/deployment configuration.
- Do not change production, simulation, Compose, or Testcontainers behavior; simulation-file edits only teach the harness about the new standard OTLP inputs.
- Preserve current `Otlp:*` validation, redaction, insecure-Production acknowledgement, sampling, health filtering, and one-shot degradation.
- Treat endpoint/protocol/headers as one coherent profile, as explicitly approved by the user on 2026-08-20.
- Keep signal-specific OTLP endpoint/protocol/header variables unsupported and untouched.
- Every new project commits its NuGet lock file; restore must pass `--locked-mode` at the final checkpoint.
- User-visible farm behavior does not change, so product glossary and in-app Help edits are not required.

---

## File Structure

### New files

- `src/Cluckwork.Api/Hosting/OtlpConfigurationResolver.cs` — pure canonical-versus-standard profile selection.
- `tests/Cluckwork.Api.IntegrationTests/OtlpSubprocessExporterTests.cs` — isolated-process proof of SDK precedence and header safety.
- `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs` — reusable collector that records paths, bodies, and request headers.
- `src/Cluckwork.AppHost/Cluckwork.AppHost.csproj` — Aspire 13.5 host project.
- `src/Cluckwork.AppHost/Properties/launchSettings.json` — Development launch profile that makes the AppHost itself load its generated user-secrets across runs.
- `src/Cluckwork.AppHost/Program.cs` — PostgreSQL, Redis, API, and Vite resource graph.
- `src/Cluckwork.AppHost/ContainerImages.cs` — exact full references plus the tag/SHA components used by Aspire.
- `src/Cluckwork.AppHost/packages.lock.json` — generated locked dependency graph.
- `tests/Cluckwork.AppHost.Tests/Cluckwork.AppHost.Tests.csproj` — process-only Aspire model tests.
- `tests/Cluckwork.AppHost.Tests/AppHostModelTests.cs` — resource, endpoint, wait, health, image, volume, and environment assertions.
- `tests/Cluckwork.AppHost.Tests/packages.lock.json` — generated locked dependency graph.
- `aspire.config.json` — rooted CLI selection of the C# AppHost.
- `web/src/test/viteConfig.test.ts` — Vite port and target precedence tests.
- `docs/runbooks/aspire-local-development.md` — prerequisites, run, observe, persist, reset, and fallback instructions.

### Modified files

- `src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs` — use the resolver and configure exporter fields unconditionally.
- `tests/Cluckwork.Api.IntegrationTests/OtlpExporterTests.cs` — pure resolver truth table, preserve disabled/malformed host checks, and remove the in-process enabled-export fixture.
- `tests/Cluckwork.Api.IntegrationTests/Infrastructure/CluckworkWebApplicationFactory.cs` — explicit canonical telemetry opt-out for hermetic tests.
- `tests/Cluckwork.Api.IntegrationTests/ProcessRoleGuardTests.cs` — rows for standard-variable endpoint and protocol failures.
- `tests/Cluckwork.Api.IntegrationTests/SchemaDocsTests.cs` — parameterize the discover-then-validate image guard and add Redis coverage.
- `tools/simulation/bootstrap.sh` — emit explicit empty standard OTLP transport inputs beside the authoritative canonical profile.
- `tools/simulation/docker-compose.sim.yml` — pass those three inputs without changing the collector topology.
- `tools/simulation/verify-harness.sh` — require them to remain empty while canonical settings are authoritative.
- `web/vite.config.ts` — consume process `PORT`, make Aspire ports strict, and preserve standalone defaults.
- `Cluckwork.sln` — include AppHost and its model-test project.
- `CONTRIBUTING.md` — add the short `aspire run` path.
- `docs/README.md` — link the local Aspire runbook.
- `.gitignore` — ignore a repository-local `.aspire/` directory only if the verified C# CLI run actually creates it; never ignore `aspire.config.json`.

## Dependency Graph

```text
OTLP resolver
  └─> unconditional SDK option setters + subprocess proofs

Generic image guard
  └─> AppHost resource graph + model tests
         └─> Vite dynamic endpoint behavior
                └─> developer docs + real runtime smoke
```

---

### Task 1: Resolve canonical and standard OTLP transport profiles

**Files:**

- Create: `src/Cluckwork.Api/Hosting/OtlpConfigurationResolver.cs`
- Modify: `src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/OtlpExporterTests.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/Infrastructure/CluckworkWebApplicationFactory.cs`

**Interfaces:**

- Produces: `internal enum OtlpTransportProfileSource { Canonical, Standard }`.
- Produces: `internal sealed record ResolvedOtlpConfiguration(OtlpOptions Options, OtlpTransportProfileSource Source)`.
- Produces: `internal static ResolvedOtlpConfiguration OtlpConfigurationResolver.Resolve(IConfiguration configuration)`.
- Consumes: existing `OtlpOptions`, including `ParseProtocol`, `ResolveTraceEndpoint`, and `ResolveMetricsEndpoint`.

- [ ] **Step 1: Add failing pure resolution tests**

Add an `OtlpConfigurationResolverTests` class to `OtlpExporterTests.cs`. Build each configuration with `ConfigurationBuilder().AddInMemoryCollection(...)`; do not use process environment. Cover this truth table:

```csharp
[Theory]
[InlineData("Otlp:Endpoint", "", false)]
[InlineData("Otlp:Protocol", "grpc", false)]
[InlineData("Otlp:Headers", "Authorization=test", false)]
public void Any_canonical_transport_key_selects_the_complete_canonical_profile(
    string canonicalKey, string canonicalValue, bool expectedEnabled)
{
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [canonicalKey] = canonicalValue,
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://standard.example:4317",
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=standard-secret",
    }).Build();

    var resolved = OtlpConfigurationResolver.Resolve(configuration);

    Assert.Equal(OtlpTransportProfileSource.Canonical, resolved.Source);
    Assert.Equal(expectedEnabled, resolved.Options.Enabled);
    Assert.NotEqual("standard-secret", resolved.Options.Headers);
}
```

Also add separate facts proving:

- no canonical transport key maps all three standard values together;
- `Otlp:AllowInsecureEndpoint` applies to the selected standard profile but does not itself select the canonical transport profile;
- a present blank canonical endpoint disables export despite a standard endpoint;
- canonical endpoint-only configuration does not inherit standard protocol or headers;
- missing settings return a disabled standard profile with the existing default protocol.

- [ ] **Step 2: Run the focused tests and observe the intended red**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter FullyQualifiedName~OtlpConfigurationResolverTests
```

Expected: compilation fails because `OtlpConfigurationResolver`, `ResolvedOtlpConfiguration`, and `OtlpTransportProfileSource` do not exist.

- [ ] **Step 3: Implement the pure resolver**

Use these exact standard keys:

```csharp
internal static class OtlpConfigurationResolver
{
    internal const string StandardEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string StandardProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    internal const string StandardHeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";

    private static readonly string[] CanonicalTransportKeys = ["Endpoint", "Protocol", "Headers"];

    public static ResolvedOtlpConfiguration Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(OtlpOptions.SectionName);
        var canonical = section.Get<OtlpOptions>() ?? new OtlpOptions();
        var hasCanonicalTransportKey = CanonicalTransportKeys
            .Any(key => section.GetSection(key).Value is not null);

        if (hasCanonicalTransportKey)
            return new(canonical, OtlpTransportProfileSource.Canonical);

        return new(new OtlpOptions
        {
            Endpoint = configuration[StandardEndpointKey],
            Protocol = configuration[StandardProtocolKey],
            Headers = configuration[StandardHeadersKey],
            AllowInsecureEndpoint = canonical.AllowInsecureEndpoint,
        }, OtlpTransportProfileSource.Standard);
    }
}
```

Keep binding inside the existing telemetry `try` block by replacing the direct `Get<OtlpOptions>()` call with `OtlpConfigurationResolver.Resolve(configuration).Options`. Do not move protocol parsing or endpoint resolution out of that boundary.

- [ ] **Step 4: Make the ordinary integration suite explicitly opt out**

In `CluckworkWebApplicationFactory.ConfigureWebHost`, add:

```csharp
// Standard OTLP variables may exist in a developer or CI environment. A
// present blank canonical endpoint selects Cluckwork's disabled profile.
builder.UseSetting("Otlp:Endpoint", "");
```

Task 1 runs only pure resolver/endpoint tests. The legacy enabled `OtlpFactory` is deleted and its boot/trace/metric coverage is moved to isolated child processes in Task 2 so standard environment behavior is hermetic and outbound payloads are causal.

- [ ] **Step 5: Run focused and existing telemetry tests green**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~OtlpConfigurationResolverTests|FullyQualifiedName~OtlpEndpointResolutionTests'
```

Expected: all selected tests pass; no process environment is changed in this task.

---

### Task 2: Override SDK-parsed transport fields and preserve process-role behavior

**Files:**

- Create: `tests/Cluckwork.Api.IntegrationTests/OtlpSubprocessExporterTests.cs`
- Create: `tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeOtlpCollector.cs`
- Modify: `src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/OtlpExporterTests.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/ProcessRoleGuardTests.cs`
- Modify: `tools/simulation/bootstrap.sh`
- Modify: `tools/simulation/docker-compose.sim.yml`
- Modify: `tools/simulation/verify-harness.sh`

**Interfaces:**

- Produces: `FakeOtlpCollector.WaitForRequestAsync(string path, TimeSpan timeout)` returning a record with `byte[] Body` and captured request headers.
- Produces: `FakeOtlpCollector.AssertNoRequestAsync(TimeSpan observationWindow)` for bounded negative assertions.
- Produces: a serving-subprocess helper that returns drained stdout/stderr for secret-leak assertions and always kills its child in `IAsyncDisposable` cleanup.
- Consumes: `OtlpConfigurationResolver.StandardEndpointKey`, `StandardProtocolKey`, and `StandardHeadersKey`.

- [ ] **Step 1: Extract and strengthen the fake collector**

Move `FakeOtlpCollector` from `OtlpExporterTests.cs` into `Infrastructure/FakeOtlpCollector.cs`. Replace the body-only task result with:

```csharp
internal sealed record CapturedOtlpRequest(
    string Path,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);
```

Capture headers with `ctx.Request.Headers.AllKeys`, preserving values without logging them. Keep `WaitForPathAsync` as a compatibility wrapper for existing tests and add `WaitForRequestAsync` for header assertions.

- [ ] **Step 2: Move every enabled-export host test out of process, then add the failing regressions**

Create `OtlpSubprocessExporterTests` using the separate-process pattern from `MultiInstanceRateLimitTests`: one pinned PostgreSQL Testcontainer, a real `dotnet Cluckwork.Api.dll` serving subprocess on a free loopback port, continuously drained stdout/stderr, `Testing` environment, generated JWT keys, migrated schema, and bounded readiness/exit cleanup. Before applying a case, remove every inherited canonical and common OTLP transport key from `ProcessStartInfo.Environment`; then add only that case's values. Never assume the child environment starts empty.

Delete `OtlpFactory` and `OtlpCollection`. Move the existing valid-endpoint boot, outbound span, and outbound metrics assertions into this subprocess class. Keep malformed/disabled configuration checks in `OtlpExporterTests`. No enabled `WebApplicationFactory` may remain anywhere in the test process; verify that with a repository search for nonblank `UseSetting("Otlp:Endpoint"` after the move.

Add these facts:

1. `Standard_profile_exports_to_the_standard_endpoint_with_its_header` — child receives only the three common standard variables; send a unique non-health request with a generated W3C `traceparent`, stop the child to flush, and prove `/v1/traces` contains that trace and `/v1/metrics` contains the expected request/runtime/DB instruments. Capture both requests and assert the test header is present with the exact value on both signals.
2. `Canonical_endpoint_never_receives_the_ambient_standard_header` — child receives canonical endpoint/protocol, plus a different ambient standard endpoint and a unique standard secret header; drive a uniquely traced non-health request, then prove the canonical collector receives both trace and metric payloads with the standard header absent from both signals, while the ambient standard collector receives no request at all.
3. `Canonical_profile_ignores_malformed_ambient_standard_transport` — canonical endpoint is valid while standard endpoint/protocol are malformed; child reaches readiness, receives a uniquely traced non-health request, and exports that exact trace to the canonical collector.
4. `Blank_canonical_endpoint_disables_an_ambient_standard_exporter` — child reaches readiness and the collector receives no request during a short bounded interval.
5. `Collector_credentials_never_appear_in_child_output` — use a unique standard-header secret, exercise both canonical and standard profiles, stop each child so exporters flush, and assert the fully drained stdout and stderr contain neither the secret value nor `x-otlp-api-key`.

Decode the OTLP protobuf payload (using the generated OTLP message types already available from the exporter dependency, or the narrowest explicit package if compilation proves they are not public) and assert the generated trace ID, an API server span, and the expected service resource. Do not accept an arbitrary nonempty export. Each child process owns its environment dictionary. Do not call `Environment.SetEnvironmentVariable` in the test process.

- [ ] **Step 3: Run the subprocess tests and observe the credential-crossover red**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter FullyQualifiedName~OtlpSubprocessExporterTests
```

Expected before the unconditional header assignment: only the canonical-header test is a new red because OpenTelemetry's `OtlpExporterOptions` constructor retains `OTEL_EXPORTER_OTLP_HEADERS`. The locked 1.17 SDK deliberately ignores malformed endpoint/protocol values, and explicit endpoint/protocol setters already override valid ambient values, so the malformed-ambient, standard-profile, and blank-disabled cases are characterization/regression proofs; do not misreport them as new reds.

- [ ] **Step 4: Assign all SDK transport fields unconditionally**

Do not clear or rewrite process environment variables. Change the exporter callback to assign all transport fields unconditionally; OpenTelemetry .NET 1.17's setters override its constructor-parsed values:

```csharp
Action<OtlpExporterOptions> ConfigureOtlpExporter(Uri endpoint) => options =>
{
    options.Endpoint = endpoint;
    options.Protocol = protocol;
    options.Headers = otlp.Headers ?? string.Empty;
};
```

- [ ] **Step 5: Add standard-variable process-role rows**

Add three rows to `ServingOnlyGuards`:

- malformed `OTEL_EXPORTER_OTLP_ENDPOINT`, with `Violate` removing all canonical transport keys before setting the bad standard endpoint;
- malformed `OTEL_EXPORTER_OTLP_PROTOCOL`, with `Violate` removing all canonical transport keys, setting a safe HTTPS standard endpoint, and setting the bad standard protocol.
- Production plaintext `OTEL_EXPORTER_OTLP_ENDPOINT` without `Otlp:AllowInsecureEndpoint`, with `Violate` removing all canonical transport keys and the acknowledgement before setting a safe-shaped HTTP standard endpoint. Its serving arm must fail the HTTPS-floor message and its one-shot arm must warn and continue; its `Satisfy` uses HTTPS or the explicit acknowledgement.

Each row's `Satisfy` removes its standard keys. Because every targeted `Violate` runs after all other `Satisfy` actions, the row cannot be masked by the existing canonical rows. Verify both generated test arms: serving boot fails with the pointed message, while `migrate` exits successfully with telemetry disabled.

- [ ] **Step 6: Teach all three simulation-harness files the new inputs**

Make the canonical simulation profile remain authoritative:

```dotenv
OTEL_EXPORTER_OTLP_ENDPOINT=
OTEL_EXPORTER_OTLP_PROTOCOL=
OTEL_EXPORTER_OTLP_HEADERS=
```

Have `bootstrap.sh` write them, `docker-compose.sim.yml` pass them to the app, and `verify-harness.sh` require all three to be present and empty whenever the canonical `Otlp__Endpoint` profile is configured. This deliberately exercises canonical precedence and unconditional SDK option assignment without changing the collector or topology.

- [ ] **Step 7: Run the focused guard and telemetry suites**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~Otlp|FullyQualifiedName~ProcessRoleGuardTests'
bash tools/simulation/verify-harness.sh
```

Expected: all tests pass; the harness verifier reports no new violation. If `verify-harness.sh` requires generated simulation state, run the documented harness bootstrap/check sequence rather than weakening the verifier.

---

### Task 3: Generalize immutable container-image guards

**Files:**

- Modify: `tests/Cluckwork.Api.IntegrationTests/SchemaDocsTests.cs`

**Interfaces:**

- Produces: a shared discover-then-validate helper parameterized by image repository, canonical full reference, and exact reviewed bare-literal allowances.
- Produces: facts `PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` and `RedisImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` using the same method.

- [ ] **Step 1: Refactor the existing PostgreSQL guard without changing behavior**

Extract the current regex sweep and tracked-file walk into a helper shaped as:

```csharp
private static void AssertImagePinIsOneIdenticalStringAcrossEveryTrackedFile(
    string repository,
    string canonicalReference,
    IReadOnlyDictionary<string, (string Value, int Count)[]> bareLiteralAllowList)
```

Build repository-specific patterns with `Regex.Escape(repository)`. Preserve every syntax class, block-scalar mask, escape refusal, interpolation refusal, occurrence-count allowance, and failure diagnostic from the current PostgreSQL test. Keep the PostgreSQL test name and canonical value unchanged.

- [ ] **Step 2: Run the PostgreSQL guard to prove the refactor is behavior-preserving**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter FullyQualifiedName~PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile
```

Expected: pass against the unchanged repository baseline.

- [ ] **Step 3: Add the Redis guard**

Use canonical Redis reference:

```text
redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2
```

Start with an empty Redis bare-literal allow-list. When Task 4 adds split Aspire declarations and independent model expectations, inspect the guard's discovery output and add exact reviewed per-file occurrence counts for the `redis` resource name in `Program.cs` and the independent `library/redis` model expectation. Do the same for new PostgreSQL literals. Never hide a whole AppHost or test file, and never let the allowance accept a tag or digest drift.

- [ ] **Step 4: Run both guards**

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile|FullyQualifiedName~RedisImagePin_IsOneIdenticalStringAcrossEveryTrackedFile'
```

Expected: both pass before AppHost files are introduced.

---

### Task 4: Build and guard the Aspire resource model

**Files:**

- Create: `src/Cluckwork.AppHost/Cluckwork.AppHost.csproj`
- Create: `src/Cluckwork.AppHost/Properties/launchSettings.json`
- Create: `src/Cluckwork.AppHost/Program.cs`
- Create: `src/Cluckwork.AppHost/ContainerImages.cs`
- Create: `src/Cluckwork.AppHost/packages.lock.json`
- Create: `tests/Cluckwork.AppHost.Tests/Cluckwork.AppHost.Tests.csproj`
- Create: `tests/Cluckwork.AppHost.Tests/AppHostModelTests.cs`
- Create: `tests/Cluckwork.AppHost.Tests/packages.lock.json`
- Create: `aspire.config.json`
- Modify: `Cluckwork.sln`
- Modify: `tests/Cluckwork.Api.IntegrationTests/SchemaDocsTests.cs`

**Interfaces:**

- Produces resources named `postgres`, `database`, `redis`, `api`, and `web`.
- Produces API endpoint named `http`.
- Produces `ConnectionStrings__Default`, `SharedState__Redis__ConnectionString`, `ASPNETCORE_ENVIRONMENT=Development`, `DOTNET_ENVIRONMENT=Development`, and `VITE_API_TARGET` expressions.

- [ ] **Step 1: Add minimal project scaffolding and solution entries**

Use this AppHost project shape:

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
    <!-- Package mode keeps ordinary/CI dotnet builds self-contained; Aspire
         13.5's ASPIRE010 only recommends CLI-bundle mode, while basic
         `aspire run` can consume the package-mode paths stamped in metadata. -->
    <AspireUseCliBundle>false</AspireUseCliBundle>
    <NoWarn>$(NoWarn);ASPIRE010</NoWarn>
    <UserSecretsId>cluckwork-apphost-local</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.JavaScript" Version="13.5.0" />
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.5.0" />
    <PackageReference Include="Aspire.Hosting.Redis" Version="13.5.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Cluckwork.Api/Cluckwork.Api.csproj" />
  </ItemGroup>
</Project>
```

The xUnit model-test project targets `net10.0`, references the AppHost, uses `Aspire.Hosting.Testing/13.5.0`, and follows existing test-project xUnit package versions. Add both projects to `Cluckwork.sln`.

Add one C# AppHost launch profile named `http` with `commandName: Project`, no fixed `applicationUrl`, no dashboard/OTLP endpoint, and both `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` set to `Development`. This is AppHost configuration, not the API child configuration. Aspire 13.5 loads the assembly `UserSecretsId` into AppHost configuration only in Development; its implicit PostgreSQL password default otherwise generates and overwrites a new stored value on every run. Keep the implicit `AddPostgres` password parameter rather than committing or printing a password.

- [ ] **Step 2: Write failing model tests before the resource graph**

Create the testing builder with:

```csharp
await using var builder = await DistributedApplicationTestingBuilder
    .CreateAsync<Projects.Cluckwork_AppHost>();
```

Inspect `builder.Resources` without starting the distributed application. Add facts that assert:

- exactly one resource for each required name;
- the launch-settings JSON has exactly one `http` project profile, both AppHost environment variables equal `Development`, and no `applicationUrl`, Aspire dashboard/OTLP endpoint, password, credential, or generated value;
- PostgreSQL's final `ContainerImageAnnotation` has `Registry=docker.io`, `Image=library/postgres`, `Tag=null`, and the exact SHA, compared against literals owned by the test rather than `ContainerImages`; locked Aspire 13.5 clears `Tag` when `SHA256` is assigned, so a simultaneous tag+SHA assertion is impossible and would test a model the SDK does not produce;
- Redis's final annotation likewise has `Registry=docker.io`, `Image=library/redis`, `Tag=null`, and the exact SHA, compared against independent test literals;
- `ContainerImages.PostgresReference` and `RedisReference` each equal an independently hardcoded full reference and also equal the repository plus their split tag/SHA components, while the structural source guard proves the split tag/SHA setters consumed by `Program.cs`; together these prove the requested tag and final digest identity without inventing a simultaneous annotation representation;
- PostgreSQL has one writable volume mount whose `Target` is `/var/lib/postgresql`;
- a boring structural source guard is bounded to exactly one PostgreSQL fluent chain and proves `WithImageTag` precedes `WithDataVolume`, which precedes `WithImageSHA256`; the reviewed tag therefore selects the PostgreSQL 18 volume target before the digest setter clears the final annotation's tag;
- Redis has no data-volume mount;
- API has a dynamic endpoint named `http`, both `/health/live` and `/health/ready` health annotations, waits for PostgreSQL and Redis, and carries both Development environment variables;
- database is referenced under connection name `Default`;
- Redis's connection expression feeds `SharedState__Redis__ConnectionString`;
- web waits for and references API, has only the endpoint supplied by `AddViteApp`, and receives `VITE_API_TARGET` from `api.GetEndpoint("http")`.
- Keep `AddViteApp`'s default npm installation: web waits for API with `WaitUntilHealthy` and for the generated `web-installer` with `WaitForCompletion`, so a clean checkout does not try to run Vite before dependencies exist.

- [ ] **Step 3: Run the model tests and observe missing-resource failures**

Run:

```bash
dotnet test tests/Cluckwork.AppHost.Tests/Cluckwork.AppHost.Tests.csproj
```

Expected: tests fail because the minimal AppHost has none of the required resources.

For the runtime-discovered launch-profile regression, first add only its structural assertion and run the focused fact before creating `launchSettings.json`. Expected: RED because the file is absent. After adding the profile, require GREEN and mutation-test removal of `DOTNET_ENVIRONMENT` before claiming the guard.

- [ ] **Step 4: Define immutable image constants**

`ContainerImages.cs` carries full canonical references for repository-wide discovery and the exact components Aspire consumes:

```csharp
internal static class ContainerImages
{
    internal const string PostgresReference =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";
    internal const string PostgresTag = "18.4-trixie";
    internal const string PostgresSha256 =
        "3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    internal const string RedisReference =
        "redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2";
    internal const string RedisTag = "7.4-alpine";
    internal const string RedisSha256 =
        "e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2";
}
```

The full references are not decorative: repository-wide guards discover them, while model tests prove the split tag/SHA inputs compose to the same identities. Aspire 13.5's final annotation retains the SHA and clears the tag; tests assert that exact final state plus the independent composition/source-order facts.

- [ ] **Step 5: Implement the resource graph**

Use this order and ownership:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag(ContainerImages.PostgresTag)
    .WithDataVolume("cluckwork-apphost-postgres-pg18")
    .WithImageSHA256(ContainerImages.PostgresSha256);
var database = postgres.AddDatabase("database", "cluckwork");

var redis = builder.AddRedis("redis")
    .WithImageTag(ContainerImages.RedisTag)
    .WithImageSHA256(ContainerImages.RedisSha256);

var api = builder.AddProject<Projects.Cluckwork_Api>("api")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(name: "http")
    .WithReference(database, connectionName: "Default")
    .WithEnvironment(
        "SharedState__Redis__ConnectionString",
        redis.Resource.ConnectionStringExpression)
    .WaitFor(postgres)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health/live")
    .WithHttpHealthCheck("/health/ready");

builder.AddViteApp("web", "../../web")
    .WithReference(api)
    .WithEnvironment("VITE_API_TARGET", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
```

If Aspire 13.5's compiler requires a different argument name or connection-expression overload, use the documented 13.5 signature that yields the same model annotation; do not weaken the model assertion.

- [ ] **Step 6: Add rooted CLI configuration**

Create:

```json
{
  "$schema": "https://aspire.dev/reference/cli/configuration/schema.json",
  "appHost": {
    "path": "./src/Cluckwork.AppHost/Cluckwork.AppHost.csproj"
  }
}
```

- [ ] **Step 7: Restore, generate lock files, and run model plus global guards**

The repository-wide image guard intentionally reads `git ls-files`. Register every intended new source, test, config, lock, design, plan, and runbook file with `git add --intent-to-add <exact paths>` before running it; this puts paths in the index without staging file contents or creating a commit. Confirm `git diff --cached` remains empty. Do not add ignored `obj/` runtime state.

Run:

```bash
dotnet restore Cluckwork.sln
dotnet test tests/Cluckwork.AppHost.Tests/Cluckwork.AppHost.Tests.csproj
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile|FullyQualifiedName~RedisImagePin_IsOneIdenticalStringAcrossEveryTrackedFile'
```

Expected: lock files are generated, model tests pass, and the only new bare image-shaped literals are the reviewed AppHost resource names and independent `library/...` model expectations with exact per-file occurrence allowances.

---

### Task 5: Make Vite honor Aspire's dynamic endpoint

**Files:**

- Modify: `web/vite.config.ts`
- Create: `web/src/test/viteConfig.test.ts`

**Interfaces:**

- Produces: `export function resolveDevServer(processEnvironment: NodeJS.ProcessEnv): { port: number; strictPort: boolean }`.
- Produces: `export function resolveApiTarget(processEnvironment: NodeJS.ProcessEnv, fileEnvironment: Record<string, string>): string`.
- Preserves: `VITE_API_TARGET` environment override and `http://localhost:8080` fallback.

- [ ] **Step 1: Add failing pure Vite configuration tests**

Cover:

```typescript
expect(resolveDevServer({})).toEqual({ port: 5173, strictPort: false });
expect(resolveDevServer({ PORT: "43210" })).toEqual({ port: 43210, strictPort: true });
expect(() => resolveDevServer({ PORT: "0" })).toThrow(/PORT/);
expect(() => resolveDevServer({ PORT: "65536" })).toThrow(/PORT/);
expect(() => resolveDevServer({ PORT: "not-a-port" })).toThrow(/PORT/);
```

Also test `resolveApiTarget` directly: process-level `VITE_API_TARGET` wins over `.env` values, file environment wins over the fallback, and the existing localhost fallback remains when neither exists.

- [ ] **Step 2: Run the Vite test red**

Run:

```bash
cd web
npm test -- --run src/test/viteConfig.test.ts
```

Expected: import/compile failure because `resolveDevServer` is not exported.

- [ ] **Step 3: Implement strict dynamic-port parsing**

Use `process.env.PORT` for Aspire's launched-process value. Accept only an integer from 1 through 65535. Set `strictPort` only when `PORT` is supplied, preserving current standalone behavior. Resolve the proxy target in this order:

1. `process.env.VITE_API_TARGET`;
2. `loadEnv(...).VITE_API_TARGET`;
3. `http://localhost:8080`.

Do not add `.WithHttpEndpoint()` to the Vite resource; `AddViteApp` already owns it.

- [ ] **Step 4: Run focused frontend verification**

Run:

```bash
cd web
npm run typecheck
npm test -- --run src/test/viteConfig.test.ts
```

Expected: typecheck and focused tests pass.

---

### Task 6: Document the workflow and prove the real stack

**Files:**

- Create: `docs/runbooks/aspire-local-development.md`
- Modify: `CONTRIBUTING.md`
- Modify: `docs/README.md`
- Modify if observed: `.gitignore`

**Interfaces:**

- Consumes: root `aspire.config.json`, resource names, dynamic endpoints, and volume name from Task 4.
- Produces: a copyable developer workflow and a safe, resolved-volume reset procedure.

- [ ] **Step 1: Write the runbook before executing it**

Document:

- prerequisites: .NET 10, Node/npm, Docker-compatible container engine, Aspire CLI 13.5;
- root command: `aspire run`;
- every `aspire wait`/`describe`/`logs`/`otel`/`stop` command targets `./src/Cluckwork.AppHost/Cluckwork.AppHost.csproj` with `--apphost`;
- `aspire describe --format Json`, AppHost-targeted unfiltered `aspire logs`, `aspire otel traces --trace-id ...`, and dashboard metrics;
- Development migrations and API user-secrets behavior;
- PostgreSQL persistence, ephemeral Redis, and the Compose dependency-only fallback;
- a reset flow that obtains the running PostgreSQL container from Aspire/Docker metadata, displays its one volume mount, validates that the mount source is neither empty nor a broad path, stops the AppHost, and removes only that exact volume.

Never document a broad recursive delete, wildcard volume deletion, remembered container ID, or credential.

- [ ] **Step 2: Install a worktree-local CLI for verification**

Run from the worktree root:

```bash
curl -fsSL https://aspire.dev/install.sh | bash -s -- \
  --version 13.5.0 \
  --install-path "$PWD/obj/aspire-cli" \
  --skip-path
obj/aspire-cli/aspire --version
```

Expected: version output begins with 13.5. The installation remains ignored under `obj/` and does not mutate the user's global Aspire installation.

- [ ] **Step 3: Start and gate the real stack**

Run:

```bash
apphost=./src/Cluckwork.AppHost/Cluckwork.AppHost.csproj
obj/aspire-cli/aspire ps --format Json --non-interactive
obj/aspire-cli/aspire run --apphost "$apphost" --detach --format Json --non-interactive
obj/aspire-cli/aspire wait postgres --apphost "$apphost" --timeout 180 --non-interactive
obj/aspire-cli/aspire wait redis --apphost "$apphost" --timeout 180 --non-interactive
obj/aspire-cli/aspire wait api --apphost "$apphost" --timeout 180 --non-interactive
obj/aspire-cli/aspire wait web --apphost "$apphost" --timeout 180 --non-interactive
obj/aspire-cli/aspire describe --apphost "$apphost" --format Json --non-interactive
```

Before `run`, fail if `ps` reports an existing instance of this exact AppHost; do not attach to or stop a run the user already owns. Put every later command in a script/shell block with a trap/finally that runs `aspire stop --apphost "$apphost" --non-interactive` only if this verification created the run. Expected: PostgreSQL, Redis, API, and web are running/healthy; the output exposes dynamic API and web endpoints.

- [ ] **Step 4: Prove Vite proxy, EF, Redis, logs, traces, and metrics causally**

From the JSON description, extract the advertised web HTTP endpoint. Generate a W3C trace ID and span ID, then POST a valid-shaped unknown-user login to the **web** endpoint's `/api/v1/auth/login` path with:

```text
traceparent: 00-<generated-32-hex-trace-id>-<generated-16-hex-span-id>-01
```

Assert status `401`, `application/problem+json`, and the exact fresh-database `Auth.NoOwnerProvisioned` title; a 429, Vite HTML response, generic proxy failure, or unrelated 401 does not pass. The generated W3C trace ID is this runtime's causal canary. The subprocess test's generated collector-header value is the automated credential non-leak canary; do not extract or print Aspire's live dashboard credential merely to duplicate that proof manually. Then:

- query `aspire logs --apphost "$apphost" --search "<generated-trace-id>" --format Json` with bounded retries and prove the correlated Serilog request line names `/api/v1/auth/login`;
- query `aspire otel traces --apphost "$apphost" --trace-id <generated-trace-id> --format Json` with bounded retries, assert the returned trace ID exactly, and require an API server span plus an EF Core or Npgsql database child span under the same trace;
- inspect the exact Redis resource container and prove the request-created `{cluckwork:win:...}` limiter key exists, using a before/after key snapshot or a unique source key so a stale key cannot pass;
- use the dashboard telemetry/API surface exposed by the verified run to record a pre-request snapshot for the current API resource instance, then assert a post-request timestamp/value delta for named request and database metrics (`http.server.request.duration`, Npgsql/DB, and EF Core). Keep current-instance filtering as an additional constraint, and separately confirm runtime metrics exist; startup/readiness samples alone cannot pass;
- search both console and structured telemetry outputs for sensitive header names including `x-otlp-api-key`, asserting none appears. Console output must come from `aspire logs`; structured OTLP logs are not expected from Cluckwork. The unique value-level leak proof remains the isolated subprocess regression above.

- [ ] **Step 5: Prove PostgreSQL restart persistence and targeted reset**

If runtime verification is resuming from the known failed pre-fix volume, do one fail-closed recovery reset first: start the exact corrected AppHost only to resolve its current PostgreSQL resource/container, require the exact pinned image identity, exactly one writable `Type=volume` mount at `/var/lib/postgresql`, a nonempty literal inspected volume name containing no `/`, then stop and confirm that exact container is not running before deleting only that validated verification-owned volume. Never delete it by its remembered name alone.

From the resulting fresh corrected run, resolve the AppHost user-secrets path with the targeted CLI, verify only that the `Parameters:postgres-password` key exists (never read, print, hash, or export its value), and snapshot the file modification time. Resolve the database connection from this run's PostgreSQL/database resource, create a uniquely named marker table or row containing a generated identifier, restart through AppHost-targeted `aspire stop` followed by AppHost-targeted `aspire run --detach`, and prove the secrets file modification time is unchanged and the identifier remains. Before removing anything, resolve the chain from this exact Aspire `postgres` resource to its current container ID; require the exact pinned image identity, exactly one writable `Type=volume` mount at `/var/lib/postgresql`, a nonempty literal Docker volume name containing no `/`, successful `docker volume inspect`, and confirmation that the resolved container is stopped. Remove only that explicit validated volume name, never use `aspire stop --force`, a wildcard, a remembered ID, or a path. Start again and prove the marker is absent while migrations recreate the current schema.

Record the exact commands that succeeded in the runbook. If the actual Aspire 13.5 JSON field names differ from the initial draft, update the runbook to the observed fields and rerun it from a clean stop.

- [ ] **Step 6: Check repository-local generated state**

Run `git status --short --ignored`. If the C# CLI created `.aspire/` under the repository, add exactly `/.aspire/` to `.gitignore`. Do not ignore `aspire.config.json`, AppHost source, lock files, or test artifacts outside existing `bin/`, `obj/`, and `node_modules/` rules.

- [ ] **Step 7: Update contributor navigation**

Add a concise Aspire path to `CONTRIBUTING.md` and link the new runbook from `docs/README.md`. State that Docker Compose remains the production-like/dependency fallback and Aspire is local orchestration only.

---

### Task 7: Run adversarial mutations and the full verification gate

**Files:**

- No new product files; temporarily mutate and restore existing AppHost/guard files.

- [ ] **Step 1: Prove each immutable-image guard goes red**

One mutation at a time, use `apply_patch`, run the focused AppHost/model and repository-wide guard tests, confirm failure names the changed field, and reverse the patch before the next mutation. For tag/SHA mutations, change only the split constant consumed by `Program.cs`; leave the independently hardcoded test expectation and full-reference constant untouched so the test cannot mutate in lockstep:

1. PostgreSQL tag `18.4-trixie` → `18.4`;
2. one hexadecimal digit of the PostgreSQL SHA;
3. Redis tag `7.4-alpine` → `7.4`;
4. one hexadecimal digit of the Redis SHA;
5. move PostgreSQL `WithDataVolume` before `WithImageTag` and confirm the structural ordering guard fails. The mount-target assertion alone is not an ordering proof because Aspire 13.5 already defaults PostgreSQL to an 18.x tag;
6. remove the AppHost launch profile's `DOTNET_ENVIRONMENT=Development` entry and confirm the exact launch-profile guard fails.

After every reversal, rerun the focused tests green. Never leave a mutation in the worktree.

- [ ] **Step 2: Run locked restore and complete .NET verification**

Run:

```bash
dotnet restore Cluckwork.sln --locked-mode
dotnet build Cluckwork.sln --no-restore
dotnet test Cluckwork.sln --no-build
```

Expected: zero warnings/errors and all tests pass, including AppHost model tests and Docker-backed integration tests.

- [ ] **Step 3: Run complete frontend verification**

Run:

```bash
cd web
npm ci
npm run typecheck
npm test -- --run
```

Expected: dependency installation succeeds and all type/tests pass. Report existing audit output separately; do not change unrelated dependencies.

- [ ] **Step 4: Run repository-specific guards**

Run:

```bash
tools/schema-docs/generate.sh --check
bash tools/simulation/verify-harness.sh
git diff --check
```

If the simulation verifier requires the full documented harness setup, execute that setup and rerun the verifier; do not bypass or weaken a Production boot guard.

- [ ] **Step 5: Re-run the real-stack smoke after all edits**

Repeat Task 6's start, web-proxied login, exact-trace query, Redis-key check, dashboard metrics check, persistence restart, and targeted reset. Stop the AppHost cleanly at the end.

- [ ] **Step 6: Inspect final scope**

Run:

```bash
git status --short
git diff --stat
git diff --check
```

Expected: only issue #565 implementation, tests, locks, and documentation are present; `obj/`, local Aspire CLI files, secrets, credentials, generated parameters, and runtime state are absent from the diff.

---

## Self-Review Checklist

- [ ] Every issue #565 acceptance criterion maps to a task above.
- [ ] Coherent OTLP profile precedence is explicitly covered by truth-table and outbound-header tests.
- [ ] OpenTelemetry SDK constructor/header-setter behavior is tested across an OS-process boundary without global environment mutation.
- [ ] All three standard-variable serving violations and one-shot degradation paths have guard rows.
- [ ] All three required simulation-harness files change together without topology drift.
- [ ] PostgreSQL and Redis tag/SHA mutations independently fail.
- [ ] PostgreSQL 18 tag/digest/volume ordering and the AppHost Development launch profile fail their structural mutation guards; the generated secret and data persist under runtime restart.
- [ ] Vite's advertised endpoint and `/api` proxy are both proven at runtime.
- [ ] Redis use is proven by a namespaced key, not resource health.
- [ ] Logs, exact trace, EF activity, and metrics are causally exercised.
- [ ] Reset resolves and validates one exact volume before deletion.
- [ ] No ServiceDefaults, production publishing, migration, domain, endpoint, or product-help changes enter scope.
- [ ] No commit or push occurs without separate user authorization.
