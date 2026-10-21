---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses a dataset on Export
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::export
- text: The Product streams the dataset as a CSV file whose cells a spreadsheet would run as formulas are prefixed to stay text
  kind: product
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::export
---

# Download one dataset

## Trigger

Someone wants the farm's data in a spreadsheet.

## Outcome

They have the CSV file.
