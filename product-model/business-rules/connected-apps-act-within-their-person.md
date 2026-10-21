---
appliesTo:
- type: context
  context:
    place: farm-mcp
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Hosting/CluckworkMcp.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/McpPage.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/McpWrite.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/McpNames.cs
- kind: doc
  role: context
  target: docs/decisions/806-mcp-endpoint.md
---

# A connected app acts with its person's role and flock scope and never more

A tool is listed and callable only when both the person's role and the permissions they allowed cover it. Each recording runs the same checks as the matching action in the web app and names inventory items and expense categories the way the app's pickers offer them: an active one by its exact name ignoring case, or any one by its id. A name several records share is refused with their ids. Reads return 50 rows unless more are asked for, at most 100, and carry no text a person typed beyond names; notes, lot numbers and expense descriptions an app sends are kept and never returned to it. While the farm's switch is off every call is refused with Connected apps are off. The person stays the actor on every audit event, with the app named beside them.
