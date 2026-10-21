---
scope: The Cluckwork API, domain and application layers, the farm web app, the MCP endpoint and the operator command verbs at c50b939c2449ca30a916099a2172cedd77d1fa56.
method: Static inspection of source, the glossary, the specification and decision records; no code was run.
covered:
- description: Domain aggregates for farms, flocks, daily entries, egg grades and lots, catalog, sales, payments, inventory, water, expenses and audit events.
  paths:
  - src/Cluckwork.Domain/
- description: Application handlers and validators for every mapped command.
  paths:
  - src/Cluckwork.Application/Modules/
- description: HTTP endpoint groups, authorization policies and the middleware that enforces tenancy, flock scope, credential epochs and the password-change gate.
  paths:
  - src/Cluckwork.Api/Modules/
  - src/Cluckwork.Api/Endpoints/CluckworkEndpointGroups.cs
  - src/Cluckwork.Api/AuthPolicies.cs
  - src/Cluckwork.Api/Middleware/
- description: MCP endpoint and tools for connected apps.
  paths:
  - src/Cluckwork.Api/Mcp/
  - src/Cluckwork.Api/Hosting/CluckworkMcp.cs
- description: Operator command verbs for farms, first admins and break-glass recovery.
  paths:
  - src/Cluckwork.Api/Cli/ProvisionAccountCliCommand.cs
  - src/Cluckwork.Api/Cli/BootstrapAdminCliCommand.cs
  - src/Cluckwork.Api/Cli/ListAccountsCliCommand.cs
  - src/Cluckwork.Api/Cli/SuspendAccountCliCommand.cs
  - src/Cluckwork.Api/Cli/ReactivateAccountCliCommand.cs
  - src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs
  - src/Cluckwork.Api/Cli/RecoverAdminCliCommand.cs
- description: Infrastructure repositories, identity services and the daily-entry lock sweep behind the mapped behavior.
  paths:
  - src/Cluckwork.Infrastructure/Modules/
  - src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs
- description: Farm web app screens, navigation and their write flows.
  paths:
  - web/src/routes/
  - web/src/App.tsx
exclusions:
- description: 'Local orchestration, analyzers, tests and test tooling: development tooling, not product behavior.'
  paths:
  - src/Cluckwork.AppHost/
  - src/Cluckwork.Analyzers/
  - tests/
  - tools/
- description: 'Deployment, CI, agent harness configuration and generated knowledge graph: project infrastructure.'
  paths:
  - deploy/
  - .github/
  - .githooks/
  - .specify/
  - graphify-out/
- description: 'Documentation, specifications, schema docs and design mockups: read as evidence, not product code.'
  paths:
  - docs/
  - specs/
- description: 'Web styling, theme, icons and translation catalogs: design and copy.'
  paths:
  - web/src/styles/
  - web/src/theme/
  - web/icons-src/
  - web/src/i18n/
- description: 'Hosting, logging, health checks, telemetry, caching and migration plumbing: runtime operation, not product behavior.'
  paths:
  - src/Cluckwork.Api/Hosting/
  - src/Cluckwork.Api/Logging/
  - src/Cluckwork.Api/HealthChecks/
  - src/Cluckwork.Api/Configuration/
  - src/Cluckwork.Api/Cli/MigrateCliCommand.cs
  - src/Cluckwork.Api/Cli/HealthCheckCliCommand.cs
  - src/Cluckwork.Infrastructure/Persistence/
- description: 'OAuth authorization-code and token exchange: the standard protocol handshake run by the OAuth library, around the modeled registration and consent.'
  paths:
  - src/Cluckwork.Api/Modules/Access/OAuth/OAuthScopeRequirement.cs
- description: 'Session refresh, refresh-token rotation and step-up grant issuance: session plumbing behind sign-in and the password re-entry the model states.'
  paths:
  - src/Cluckwork.Api/Modules/Access/Auth/AuthEndpoints.Session.cs
  - src/Cluckwork.Api/Modules/Access/Auth/AuthCookies.cs
  - src/Cluckwork.Api/Modules/Access/Auth/StepUpRequestValidator.cs
- description: 'Request idempotency replay and per-farm report concurrency limiting: transport safety nets, not farm behavior.'
  paths:
  - src/Cluckwork.Api/Middleware/IdempotencyMiddleware.cs
  - src/Cluckwork.Api/Middleware/IdempotencyOptions.cs
  - src/Cluckwork.Api/Middleware/IdempotentUnit.cs
  - src/Cluckwork.Api/Modules/Insights/Reports/ReportConcurrencyLimitFilter.cs
- description: 'Demo and simulation seeding verb: sample data for demos and development.'
  paths:
  - src/Cluckwork.Api/Cli/SeedCliCommand.cs
- description: 'Background purges of idempotency records, refresh tokens and unapproved OAuth data: housekeeping.'
  paths:
  - src/Cluckwork.Infrastructure/Jobs/IdempotencyRecordPurgeSweep.cs
  - src/Cluckwork.Infrastructure/Jobs/RefreshTokenPurgeSweep.cs
  - src/Cluckwork.Infrastructure/Jobs/OAuthPurgeSweep.cs
  - src/Cluckwork.Infrastructure/Jobs/DurableJobWorker.cs
- description: 'In-app Help page and glossary: documentation of the product rather than behavior of it.'
  paths:
  - web/src/routes/HelpPage.tsx
  - web/src/routes/help/
- description: 'Home-screen install and new-version prompt: a browser platform feature that keeps no farm data.'
  paths:
  - web/src/pwa/
- description: 'Client error reporting: telemetry for the team running the server.'
  paths:
  - src/Cluckwork.Api/Endpoints/ClientErrors/
unmapped:
- description: 'The hand-off from a one-time-password sign-in to the Set your password screen as a Journey: pdd-kit refuses a Journey whose person signs in partway, so it is stated in the sign-in and change-password scenarios instead.'
  paths:
  - src/Cluckwork.Api/Middleware/MustChangePasswordMiddleware.cs
limitations: []
---

# Coverage
