# The decision (#788)

**Build an OAuth 2.1 authorization server with OpenIddict**, running alongside the existing login and
serving third-party clients only.

**No incident.** This is a forward-looking choice, not a rule earned by a defect. A reader is entitled
to know that before treating anything here as load-bearing.

## The question

An MCP client — an AI assistant — could not authenticate to Cluckwork at all. The access token lives
15 minutes and rotation happens through an HttpOnly refresh cookie, which is a browser mechanism. An
assistant pasting a bearer would stop working within the quarter-hour and have no way to renew.

## What was rejected, and it was rejected late

A **Personal Access Token** design was worked through to completion before being set aside: GitHub-style,
self-service on the Account page beside change-password, an opaque 256-bit random with only a SHA-256
hash stored, a `cw_pat_` prefix plus CRC32 checksum so secret scanners can spot it and typos are
rejected without a database lookup, mandatory capped expiry, Owner-wide visibility and revocation, and
revocation semantics matching GitHub's — not killed by a password or role change, because roles are
read live and a demoted user's credential narrows on its own.

It was workable and it was cheaper, by roughly half.

It lost on one argument. **A PAT is a workaround for MCP clients not being able to do OAuth, and the
reason MCP expects OAuth is that OAuth is the better answer** for delegated, revocable, scoped access
to someone else's data. Choosing the workaround means handing a long-lived credential to a robot and
hoping nobody loses it. The counter-argument — that "we will do it properly later" usually means never
— was stated and weighed.

The PAT design is preserved in [`03-libraries.md`](03-libraries.md) rather than discarded, because if
OAuth ever proves too heavy those conclusions are reusable.

## Why OpenIddict specifically

**Microsoft's own documentation settles the build-versus-buy question.** The Identity API's bearer
tokens are described verbatim as *"not intended to be a full-featured identity service provider or
token server"* — a short-lived-access plus refresh **session** pattern for clients that cannot hold
cookies. No named tokens, no per-token scopes, no independent revocation. **ASP.NET Core Identity has
no PAT or OAuth-server primitive at all**, so the real options were OpenIddict or writing an OAuth
server by hand.

OpenIddict 7.7.0 is **Apache-2.0** — free, permissive, no commercial restriction — confirmed `net10.0`
on NuGet, actively maintained with corporate sponsors, and provides authorization-code with PKCE,
client credentials, the device flow and discovery metadata.

**Duende IdentityServer was priced and rejected**: $5,750/yr at the Lite tier (2 client IDs), rising to
$24,900. Disproportionate for a farm application.

## Accepted risks

- **OpenIddict has one primary maintainer.** That is a real bus-factor risk on a security-critical
  dependency. Apache-2.0 means a fork is legally possible, though forking an OAuth server is not a
  small undertaking. Accepted because the alternative — hand-rolling OAuth — carries more risk, not
  less, and this is the ecosystem's standard implementation.
- **Two authentication paths will coexist.** The SPA keeps its hand-rolled login; OpenIddict serves
  assistants. That is duplication in the most security-sensitive area of the app. Accepted because
  rewriting the existing login means re-proving every invariant it carries — credential-epoch
  revocation (#364), account lockout (#128), ambient-principal blanking (#532), farm-code sign-in
  (#530), refresh rotation with reuse detection, step-up grants (#308/#338), must-change-password
  (#283) — and doing that *at the same time* as introducing OpenIddict would give the two changes one
  blast radius. Revisiting is tracked as
  [#793](https://github.com/mforce/cluckwork/issues/793), with the conditions that would justify it
  **and** the conditions under which it would be a bad idea.
- **Two key-management stories will coexist.** The hand-rolled RSA PEM pair with its boot check (#510),
  and OpenIddict's own credential management. Neither knows about the other. Recorded on
  [#268](https://github.com/mforce/cluckwork/issues/268) so a future reader does not assume one system
  governs signing keys.
- **The cost is roughly double the rejected option.** Chosen with that understood.
