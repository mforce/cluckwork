---
availability:
- place: operator-cli
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/ProvisionAccountCliCommand.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Access/Identity/AccountProvisioner.cs
---

# Provision a farm

Create a new farm with its farm code, its ten standard egg grades, its six packed units and its first Owner, in one transaction. The Owner gets a printed one-time password. The operator may set the farm's timezone, locale and currency; any left out default to UTC, en-US and USD.
