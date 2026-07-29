---
type: "query"
date: "2026-07-29T07:54:55.999648+00:00"
question: "What is the exact relationship between the SPA's hand-written API client and the tech spec's KD-6 OpenAPI-generated typed client decision?"
contributor: "graphify"
source_nodes: ["OpenAPI-Generated Typed Client (KD-6)", "Cluckwork Web SPA README", "apiFetch()", "client.ts", "cluckwork.ts"]
---

# Q: What is the exact relationship between the SPA's hand-written API client and the tech spec's KD-6 OpenAPI-generated typed client decision?

## Answer

KD-6 (tech_spec.md §8.2, 'OpenAPI-Generated Typed Client') is a drifted/unimplemented decision. The KD-6 rationale node has degree 1: its only edge is an AMBIGUOUS conceptually_related_to from the Web SPA README (web/README.md), flagged during extraction because the README documents a hand-written fetch client while KD-6 promises a generated one. No code node carries an implements edge to KD-6. The actual client is hand-written: web/src/api/client.ts with apiFetch() at L326 as the hub (11 EXTRACTED structural edges — apiGet/apiPost/apiPut/apiDelete/apiPutBytes call it; it calls refreshTokens, currentAccessToken, clearAccessToken, isTransientRefreshFailure) plus ~100 hand-written endpoint wrappers in web/src/api/cluckwork.ts (createOrder L285, recordPayment L818, recordDailyEntry L142, etc.) spanning the Sales/Inventory/Daily-Entry SPA communities. Structural conclusion: a spec decision node with no implementation edges sits beside a densely-connected hand-rolled implementation — the divergence is real, and either KD-6 should be amended/retired in the tech spec or the generated-client migration is outstanding work.

## Source Nodes

- OpenAPI-Generated Typed Client (KD-6)
- Cluckwork Web SPA README
- apiFetch()
- client.ts
- cluckwork.ts