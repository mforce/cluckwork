---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses the full export
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::export
- text: The Product builds one zip with a CSV file per dataset and a manifest of row counts
  kind: product
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::export
---

# Download everything

## Trigger

Someone wants a manual backup of the farm.

## Outcome

They have the zip.
