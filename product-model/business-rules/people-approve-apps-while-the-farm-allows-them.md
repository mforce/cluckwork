---
appliesTo:
- type: entity
  id: app-connection
  effect: creates
permits:
- actors:
  - owner
  - manager
  - worker
  - sales
  - read-only
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
---

# Anyone on the farm approves an app for themselves while the farm allows connected apps

Every role may connect an app to act as themselves. While Allow connected apps is off the consent screen refuses every app.
