---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/BootstrapAdminCliCommand.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/FirstRunAdminService.cs
- kind: doc
  role: context
  target: docs/decisions/283-first-run-admin-provisioning.md
---

# Provision the first admin

Create the default farm's first Owner on a fresh server with a generated password printed to standard output only, together with the farm code.
