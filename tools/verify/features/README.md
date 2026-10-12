# Feature map

One file per user-facing feature. Each answers, from the user's side: what it is, how to
reach it, how to drive it with the harness in `tools/simulation/ui/`, and what end state
proves it works. A proof that drives one entry point is incomplete when the file lists
others. Keep this map honest with `/maintain-verification-skill` when the app changes.

| Feature | Route | Personas | Existing specs |
| --- | --- | --- | --- |
| [Dashboard](dashboard.md) | `/` | every role | `owner.spec.ts`, `phone.spec.ts`, `readonly.spec.ts` |
| [Daily entry](daily-entry.md) | `/daily-entry` | Worker, Manager, Owner | `worker.spec.ts`, `manager.spec.ts`, `phone.spec.ts` |
| [Sales](sales.md) | `/sales`, `/customers` | Sales, Manager, Owner | `sales.spec.ts`, `worker-sale-allocation.spec.ts` |
| [Stock](stock.md) | `/stock`, `/inventory` | every role reads; Manager, Owner write | `manager.spec.ts`, `readonly.spec.ts`, `pagination.spec.ts` |
| [Flocks](flocks.md) | `/flocks` | Manager, Owner | `manager.spec.ts`, `named-entity-picker.spec.ts` |
| [MCP server](mcp.md) | `/mcp`, `/connect` | every role, through a connected app | none; real clients in `tools/verify/mcp/` |
| [Products](products.md) | `/products` | Admin, Manager (nav); others can deep-link read-only | `inspector-layout.spec.ts`, `phone.spec.ts` |
| [Audit](audit.md) | `/audit` | Admin, Manager; others refused by the API | `owner.spec.ts`, `readonly.spec.ts` |

Not yet mapped (routes exist, add a file when you first verify one): `/feed`, `/water`,
`/history`, `/reports` (`reports-range.spec.ts`), `/expenses`, `/settings` (Owner only,
#729), `/grades`, `/users`, `/export`, `/account`, `/help`.

Cross-cutting guarantees with their own specs, not features: session refresh and races
(`session-*.spec.ts`), i18n across es and tl (`i18n.spec.ts`), the PWA shell offline
(`pwa.spec.ts`), the CSP nonce (`csp-nonce.spec.ts`), live regions under a modal
(`a11y-live-regions.spec.ts`).
