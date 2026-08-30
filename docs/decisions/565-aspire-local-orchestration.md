# Aspire is local orchestration, and it is a second database (#565)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-08-30

## What happened

**No product incident.** #565 added a .NET Aspire 13.5 AppHost as an *additive*
local path: it starts the existing PostgreSQL, Redis, API and Vite resources and
changes nothing about production, simulation, Testcontainers or the API's HTTP
surface. The design is in
[`docs/superpowers/specs/2026-08-19-aspire-apphost-design.md`](../superpowers/specs/2026-08-19-aspire-apphost-design.md).

There is one developer-facing failure behind this record, from 2026-08-30.
`bootstrap-admin` was run against a live Aspire stack and failed twice with two
different messages before anyone suspected the database:

```text
Bootstrap failed: Failed to connect to 127.0.0.1:5432
Bootstrap failed: 28P01: password authentication failed for user "cluckwork"
```

Neither says what is actually wrong. Aspire had started its **own** PostgreSQL —
its own container, its own volume, the username `postgres`, and a password
generated once into the **AppHost's** user-secrets. `bootstrap-admin` is a
run-then-exit process that the AppHost never launched, so nothing injected that
connection string; the verb fell through to the **API's** user-secrets
`ConnectionStrings:Default`, which names the Compose dev stack's
`cluckwork`/`cluckwork` credential on `localhost:5432`. The first message was
the two stacks sitting on different ports. The second appeared after the
committed `LocalPorts` were edited to `5432`/`6379`, which put both stacks on one
port and turned a connection error into an authentication error — the same
misunderstanding, now one step harder to see. The runbook had three forms and
none of them was this one.

## The rule

The Aspire AppHost is **local orchestration only** — never a deployment path,
and never a second definition of how the app boots. It starts its own PostgreSQL
on its own volume (`cluckwork-apphost-postgres-pg18`) with a **generated**
password held in the *AppHost's* user-secrets under
`Parameters:postgres-password` and the username `postgres`, so it is a **second,
separate database** from `deploy/docker-compose.dev.yml` — an Owner provisioned
in one does not exist in the other. Aspire injects that connection string into
the `api` resource **it launches**, and into nothing else: any run-then-exit verb
(`bootstrap-admin`, `seed`, `recover-admin`, `migrate`) started by hand is
outside the AppHost's process graph and must be **given** the connection string
explicitly, or it silently addresses the Compose database instead. The committed
`LocalPorts` defaults must therefore stay clear of the ports Compose publishes;
a machine that needs different ports overrides them in user-secrets, an
environment variable or a run argument — **never by editing the committed file**.
And because Aspire wires the API's configuration by hand
(`WithReference(database, connectionName: "Default")`, the
`SharedState__Redis__ConnectionString` environment entry), a new **required**
config key or boot guard must be taught to the AppHost in the same PR, exactly as
[#370](370-sim-harness-boot-guards.md) requires for the simulation harness.

The AppHost's **own** endpoints — the dashboard, its OTLP receiver and the
resource service — are not resources, so `LocalPorts:*` cannot reach them and
Aspire otherwise assigns each a random free port per run. They are pinned in
`Properties/launchSettings.json` (dashboard `18888`, OTLP `18889`, resource
service `18890`) so a bookmarked dashboard URL and a copied OTLP endpoint stay
valid across restarts. Only the host:port is stable: the dashboard's login token
is minted per run and stays a secret taken from the CLI. All three are plaintext
`http://` loopback, which Aspire refuses unless the profile also sets
**`ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`** — a deliberate local-only
acceptance, bounded by loopback and by the AppHost never being a deployment
path. **Moving those three URLs to `https://` is what retires that flag**, and
the guard asserts the flag and the `http://` scheme together so the pair cannot
drift apart.

## Why not the obvious alternative

**Point Aspire at the Compose ports so one connection string serves both.** This
is what the 2026-08-30 edit tried, and it is worse than the problem it solves.
`ConnectionStrings:Default` in the API's user-secrets is a single value naming
`localhost:5432`; if both stacks can hold that port then that value means
"whichever stack happens to be up", the two databases become
indistinguishable to every hand-run verb and IDE debug session, and a write
lands in whichever one answered. The failure is silent and the wrong database
looks healthy. Keeping the ports disjoint is what makes the mistake *loud* — it
is why the guard bans `5432` and `6379` by number rather than merely documenting
a preference.

**Have the verbs inherit Aspire's configuration.** They cannot: they are not
resources. Modelling each one as an Aspire resource with an explicit start was
considered and rejected as more machinery than a documented environment-variable
override — see form 4 of
[the first-admin runbook](../runbooks/first-admin-provisioning.md#4-aspire-apphost-stack).

**Pin the host endpoints in `appsettings.json` beside `LocalPorts`.** This does
work, and was measured rather than assumed: a zero-resource probe AppHost run
with `--no-launch-profile` and the three variables unset in the environment
bound all three endpoints from `appsettings.json` alone — but only under the
literal keys `ASPNETCORE_URLS`, `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` and
`ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL`. The tidy-looking host key `urls` is
**ignored** by the dashboard, which logs `Generating a dynamic url for dashboard`
and takes a random port — a silent half-failure, since the other two keys still
apply. Rejected anyway: `appsettings.json` is copied to the output directory and
would carry four SHOUTY environment-variable names as configuration keys, and a
launch profile is the file whose entire job is composing the launcher's
environment. `LocalPorts` describes *resources*; these describe the *host*.

**Adopt Aspire ServiceDefaults and client integrations.** Rejected at design
time (option C in the design doc): it replaces working Cluckwork infrastructure,
pulls Aspire into the production application, and would make Aspire a second
definition of health, telemetry and data access rather than an orchestrator of
the existing one.

## What this does NOT cover

Aspire changes nothing about production, the Compose stacks, the simulation
harness, or Testcontainers-based integration tests; there is no Aspire publish
or deploy path and adding one is a separate decision. The port guard pins the
**committed defaults** only — a local override to any port, including a
colliding one, is deliberately legal and unguarded, because that is the escape
hatch for a machine where a pinned port is taken. Nothing detects a *runtime*
collision: if you override `LocalPorts:Postgres` to `5432` on your own machine,
the ambiguity described above is yours to manage. Nothing enforces that Aspire
runs at all — Compose remains a fully supported path, so a change that breaks
only `aspire run` is caught by no CI job.

## How it is enforced

- [`tests/Cluckwork.AppHost.Tests/AppHostConfigurationTests.cs`](../../tests/Cluckwork.AppHost.Tests/AppHostConfigurationTests.cs) —
  `Committed_appsettings_pins_every_local_port_to_a_usable_value` fails when a
  committed `LocalPorts` value is unparseable, outside `1024–32767`, duplicated,
  or equal to `5432`/`6379`; `Committed_appsettings_is_a_live_configuration_source`
  and `AppHost_project_copies_appsettings_to_the_output_directory` keep the file
  actually reaching the AppHost.
- [`tests/Cluckwork.AppHost.Tests/AppHostModelTests.cs`](../../tests/Cluckwork.AppHost.Tests/AppHostModelTests.cs) —
  `Model_declares_the_exact_expected_resources_annotations_and_relationships`,
  `Api_and_web_model_the_required_expressions`,
  `Local_port_configuration_pins_every_host_port`,
  `Unparseable_local_port_configuration_falls_back_to_the_dynamic_default`,
  `Container_images_are_exact_pinned_references_with_split_components` and
  `Postgres_volume_and_fluent_declaration_order_are_version_safe`;
  `AppHost_launch_profile_pins_host_endpoints_and_runs_in_Development` pins the
  three host endpoints by exact URL, keeps both key sets exhaustive so an
  unreviewed launch-profile addition goes red, and asserts
  `ASPIRE_ALLOW_UNSECURED_TRANSPORT` beside the `http://` scheme it exists for.
- `SchemaDocsTests`' repository-wide image-pin guards understand Aspire's split
  `WithImageTag`/`WithImageSHA256` representation, so an AppHost image cannot
  drift from the Compose pins.

**Nothing enforces the "teach the AppHost about a new config key" half of this
rule; it relies on review** — the same exposure #370 records for the simulation
harness, and for the same reason: the AppHost is deliberately not in CI.
