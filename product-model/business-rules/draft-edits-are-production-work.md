---
appliesTo:
- type: entity
  id: daily-entry
  effect: changes
  to: Draft
permits:
- actors:
  - owner
  - manager
- actors:
  - worker
  when:
  - entity: worker
    fact: Flock scope
    is: All flocks
- related:
  - &id001
    verb: has
    entity: flock
  - verb: scopes
    entity: flock-assignment
  - verb: holds
    entity: user
  - verb: signs-in-as
    entity: worker
- related:
  - *id001
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: owner
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Record daily entries
    is: Allowed
- related:
  - *id001
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: manager
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Record daily entries
    is: Allowed
- related:
  - *id001
  - verb: keeps
    entity: farm
  - verb: employs
    entity: user
  - verb: signs-in-as
    entity: worker
  - verb: approves
    entity: app-connection
  - verb: holds
    entity: ai-agent
  when:
  - entity: farm
    fact: Connected apps
    is: 'On'
  - entity: app-connection
    fact: Record daily entries
    is: Allowed
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Owners, Managers, Workers within their flock scope, and their connected apps edit a draft daily entry

Saving over a Draft follows the same grants as starting one.
