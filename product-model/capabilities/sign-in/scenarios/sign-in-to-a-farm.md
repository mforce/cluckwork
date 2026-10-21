---
kind: primary
routes:
  web: Web
steps:
- text: The Visitor enters the farm code, then the email and password, and signs in
  kind: actor
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts: []
  - entity: user
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The Product checks the credentials against that farm's people and starts a session in this browser tab
  kind: product
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
  - entity: user
    effect: reads
    facts:
    - Email
    - Password
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The person lands in the app, after the farm's banner splash when one is set
  kind: condition
  entities:
  - entity: farm
    effect: reads
    facts:
    - Banner
  contexts:
    web:
      place: farm-web::public::sign-in
---

# Sign in to a farm

## Trigger

Someone starts working on a farm.

## Outcome

They are signed in to that farm with their role.

## Edge cases

- A link ending in ?farm=<code> prefills the farm code; an invalid code in the link is ignored.
- One browser can hold separate sessions for several farms.
