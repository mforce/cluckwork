# HTTP Contract: Named Entity Discovery and Row Display

All routes remain under `/api/v1`, retain their existing authorization, and use the current tenant/flock-scope query filters. Changes are additive except for newly invalid conflicting flock query parameters.

## Flock Discovery

### `GET /api/v1/flocks`

Query parameters:

| Parameter | Type | Default | Contract |
|---|---|---|---|
| `search` | string? | unfiltered | Trimmed, case-insensitive literal substring of `name`; blank is unfiltered; `%`, `_`, and `\` are literals |
| `eligibility` | enum? | `active-and-depleted` | Exact values: `active`, `active-and-depleted`, `all` |
| `includeArchived` | boolean? | absent | Legacy alias: `true` means `all` only when `eligibility` is absent |
| `limit` | integer? | 100 | Existing clamp: 1..500 |
| `offset` | integer? | 0 | Existing floor: 0 |

Evaluation order:

1. Existing tenant and flock-scope structural filter.
2. Eligibility predicate.
3. Literal name search when non-blank.
4. `ORDER BY Name, Id`.
5. Offset and limit.

Response: existing bare `FlockResponse[]`; no envelope or total is added.

Invalid requests return the existing validation-problem 400 response:

- unknown `eligibility`;
- both `eligibility` and `includeArchived` are present, even if `includeArchived=false`.

Compatibility examples:

- `/flocks` continues to include Active and Depleted and exclude Archived.
- `/flocks?includeArchived=true` continues to include all statuses.
- `/flocks?limit=500` and all current k6/SPA callers remain valid.

## Customer Discovery

### `GET /api/v1/customers`

Authorization remains `SalesFlow` because customer rows contain PII.

Query parameters:

| Parameter | Type | Default | Contract |
|---|---|---|---|
| `search` | string? | unfiltered | Same literal trimmed case-insensitive substring behavior as flocks |
| `limit` | integer? | 100 | Existing clamp: 1..500 |
| `offset` | integer? | 0 | Existing floor: 0 |

Evaluation order: existing tenant filter, search, `ORDER BY Name, Id`, offset, limit.

Response: existing bare `CustomerResponse[]`; no envelope or total is added.

## Exact Resolution

Existing routes are unchanged and receive new SPA wrappers:

- `GET /api/v1/flocks/{id}` -> full `FlockResponse` or 404.
- `GET /api/v1/customers/{id}` -> full `CustomerResponse` or 404; `SalesFlow` authorization remains.

Foreign-tenant, flock-scoped-out, missing, or unauthorized identities never return identifying data. Picker adapters translate 404/denial into the page's explicit unavailable state; they never substitute the first discovery result.

## Additive Row Responses

| Route/response | Added JSON fields |
|---|---|
| Daily Entry list/detail response | `flockName: string`, `flockStatus: string` |
| Feed usage list response | `flockName: string` |
| Water usage list response | `flockName: string` |
| User flock assignment response | `flockName: string | null` |
| Sales order list/detail response | `customerName: string` |
| Expense list/detail/adjust response | `flockName: string | null` |

Rules:

- Names and flock status are current scoped display data, not historical snapshots.
- Each returned page resolves distinct referenced IDs in one scoped bulk operation.
- Flock movement aggregation for the flock list is restricted to returned flock IDs.
- User assignments remain unpaged and resolve assignment/flock name in one scoped left-join projection.
- `null` assignment name pairs with `flockId=null` for farm-wide scope.
- `null` Expense flock name pairs with `flockId=null` for None.
- Required names are never replaced with identifier fragments.
- Existing fields, paging envelopes, status codes, and write bodies are unchanged.

### The defensive `null` name on a non-null id

`flockName`/`customerName` are declared nullable in the response records even where
the table above says `string`, and that is a repository fact rather than a claim
about this API's HTTP behaviour. The bulk reference read resolves ids through the
same tenant + flock-scope filter that guards the list routes (#613), so a key can
legitimately be absent while the referring row is still returned — a flock that
left the caller's scope between the page read and the reference read, for
instance. The read returns a missing key; it does not fabricate a label, and the
endpoint renders the resulting `null`.

On the routes themselves this is unreachable, and deliberately so: every flock-
naming list route is already behind the scope the reference read re-applies, so a
Worker never receives a row whose flock is out of scope, and an Owner or Manager is
scope-unrestricted. The `null` therefore describes a defensive repository case that
the guards pin at the repository (`FlockReferenceRead_RespectsFlockScopeForAWorker`,
`BulkReferenceReads_AreTenantScopedNotResponseScoped`), not an HTTP state an SPA
can be routed into. An SPA may treat a required name as present; if one ever
arrives `null`, that is a scope defect at the read, and the correct SPA behaviour
is the explicit unavailable state — never an identifier fragment.

## Error and Paging Semantics

- A 50-row picker request with 50 results may be followed by one additional request; an empty additional page ends paging.
- Replacement and extension errors use existing HTTP error responses; the picker distinguishes them by the operation it initiated.
- URL malformed-ID rejection is client-side for Sales and is never sent to the Sales list or customer exact route.
- A well-formed unavailable Sales customer ID may invoke exact resolution but must not invoke a filtered Sales list until availability is established.
