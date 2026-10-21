---
type: agent
actors:
- ai-agent
entryPoints:
- agent: /mcp
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Hosting/CluckworkMcp.cs
- kind: doc
  role: context
  target: docs/decisions/806-mcp-endpoint.md
- kind: doc
  role: context
  target: docs/decisions/1311-mcp-write-tools.md
---

# Farm MCP address

The farm's MCP address, Cluckwork's own address followed by `/mcp`, where a
connected app a person approved reads farm data, records and submits daily
entries, and records feed use, water use, inventory purchases and expenses for
that person, each only with the permission the person allowed.

## Intent

Let a person's own AI assistant do farm reads and the day's recording, with the
person's own role and flock scope and never more. A daily entry still starts as a
Draft and reaches stock only when it is submitted. Feed, water, purchases and
expenses count at once, as the app's own buttons do, and each reply says where an
Owner or Manager corrects the record.
