---
id: cluckwork
summary: 'Poultry farm management for egg-producing layer operations: daily production by grade, egg-lot traceability from flock to sale, sales with FIFO allocation, and the farm''s numbers.'
category: agriculture
tags:
- poultry
- egg-production
- farm-management
- inventory
- sales
authors:
- name: mforce
  url: https://github.com/mforce/cluckwork
license: MIT
languages:
- en
- es
- tl
limitations:
- Recorded bird, egg and feed movements are never edited or deleted; every correction is a new compensating record.
- 'There is no emailed password reset: an Owner sets a colleague''s password, and an operator resets a sole Owner''s password from the server.'
- A farm's currency cannot change once any amount has been recorded in it.
- Only egg products are sold, and each product draws stock from exactly one egg grade.
- A farm's code is changed only by an operator on the server, never from the app.
references:
- kind: doc
  role: context
  target: README.md
- kind: doc
  role: intent
  target: specs/product/specs.md
  title: Product and technical specification
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
  title: Glossary
---

# Cluckwork

Cluckwork runs a poultry farm's daily operation from one system, starting with
egg-producing layer flocks. People on a farm record each flock's day of
production by grade, which turns sellable eggs into egg lots traceable from the
flock and day that laid them through to the sales order that sold them. Sales
orders draw those lots oldest first, payments settle confirmed orders, and the
farm sees its stock on hand, lay rate and money in one place. Feed, water,
expenses, users and connected AI apps sit around that core, and operators run
farms on a shared server from the command line.

## Intent

The project's README puts it as helping "a farm run its daily operation from one
system: record production, track egg lots from the hen through to the sale with
full traceability, block medication-restricted lots, manage sales and customers,
and see the numbers that matter (hen-day rate, saleable %, stock on hand)".
Recording a day closes the loop: a house the Dashboard flags as unreported is
counted, graded and submitted, which creates its egg lots and moves the
Dashboard's totals. The design favours append-only ledgers and compensating
corrections so that every balance can be rebuilt and every number explained.
