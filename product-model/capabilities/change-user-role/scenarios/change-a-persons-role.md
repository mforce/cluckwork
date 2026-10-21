---
kind: primary
routes:
  web: Web
steps:
- text: The Owner chooses role on another person, picks the new role and re-enters their own current password
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product changes the role and ends the person's sessions
  kind: product
  actor: owner
  entities:
  - entity: user
    effect: changes
    facts:
    - Role
  contexts:
    web:
      place: farm-web::setup::users
---

# Change a person's role

## Trigger

A person's job on the farm changes.

## Outcome

Their next request uses the new role; the change is audited old to new.

## Edge cases

- A promoted Worker's flock assignments go inert and come back if they are demoted to Worker again.
