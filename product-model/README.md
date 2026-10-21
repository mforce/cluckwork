# Product model

This folder describes what Cluckwork does today: what it keeps, what each action does, who may do it, and when the app refuses it. The pages are Markdown, most with YAML front matter, and most link to the code behind them.

It uses the format of [pdd-kit](https://github.com/itai-gendler/pdd-kit). The repo does not depend on pdd-kit's CLI or skills. To browse the model as a report, you can run `npx pdd-toolkit view mforce/cluckwork`. It is optional.

## How to read it

Start with `product.md`, then:

| Folder | Holds |
|---|---|
| `entities/` | What the app keeps, with each entity's facts and states |
| `capabilities/` | What a person can do, with `scenarios/` that include the refusals |
| `business-rules/` | What must always hold, and who may perform each operation (`permits`) |
| `domains/` | Subject areas that group capabilities. They are not code modules. |
| `interfaces/` | The farm web app, the farm MCP address and the operator commands |
| `journeys/`, `variations/` | A goal across several capabilities, and behavior that works more than one way |

`coverage.md` lists which code the model accounts for. It does not say the code matches.

## When the model and the code disagree

The code decides what the app does. When the model disagrees with it, check the code, `specs/product/specs.md` and `specs/product/GLOSSARY.md`, then fix whichever is wrong. If the model describes the intended behavior and the code is wrong, keep the model and open an issue for the code.

## Keeping it current

A PR does not have to update the model, even when it changes behavior or moves code the model links to. The maintainer checks the model for drift, including links to code that moved, and fixes it in batches.
