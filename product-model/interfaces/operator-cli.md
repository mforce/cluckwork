---
type: cli
actors:
- operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Cli/CliDispatcher.cs#CliDispatcher
- kind: doc
  role: context
  target: docs/runbooks/README.md
---

# Operator commands

One-shot command verbs an operator runs at a shell on the deployment to provision farms and their first Owner, list, suspend, reactivate and rename farms, and recover a locked-out Owner. Each prints what it did to standard output only.
