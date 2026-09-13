# How it works (#788)

## The flow, from a user's side

An assistant registers itself, then sends the user to Cluckwork to approve it. The user sees one
screen, re-enters their password, and clicks Allow. The assistant then works until disconnected.

```
assistant ──register──▶ Cluckwork            (grants nothing on its own)
assistant ──send user─▶ /connect             (SPA consent screen)
user      ──password──▶ step-up grant        (#308's existing mechanism)
user      ──Allow─────▶ authorization        (recorded against the user)
assistant ──exchange──▶ reference token      (works until revoked)
```

## Decisions

| | Decision | Why |
|---|---|---|
| **Scope of the server** | Third-party clients only | The SPA's login carries too much earned behaviour to rewrite alongside this. [#793](https://github.com/mforce/cluckwork/issues/793) |
| **Client registration** | Dynamic self-registration, rate-limited, unapproved apps expire | What the MCP spec recommends. Registration grants **zero** access, so the risk is junk rows, not unauthorized access |
| **Who may connect** | Any user, for their own data | A Worker's assistant gets a Worker's view. Effective permission is always `role ∩ scope` |
| **Approval** | Re-enter current password | Reuses #308's step-up grant. Stops someone at an unlocked laptop silently connecting an assistant |
| **Scopes** | Two: read farm data, record daily entries | Matches the 7-tool phase-1 surface. A consent screen with eight checkboxes is one people click through without reading |
| **Token format** | **Reference tokens, not JWTs** | So disconnecting takes effect on the *next call*. A self-contained token would keep working until expiry, contradicting #364's guarantee that revocation is immediate |
| **Lifetime** | Indefinite until revoked | The point of choosing OAuth: the assistant keeps working without anyone pasting anything |
| **Visibility** | Users manage their own; an Owner sees and revokes any | Owners already reset passwords and disable users, so this is *less* invasive than powers they hold. Prevents a departed employee's assistant staying connected unseen |
| **Audit** | First-class columns recording which app acted | Provenance is *who acted*, not a detail — and it must be filterable, which `DetailsJson` is not |
| **Discovery** | The MCP SDK's `McpAuthenticationHandler` | What lets an assistant connect by itself rather than being hand-configured |

## Scopes narrow, never widen

Effective permission is **`role ∩ scope`**. A scope can only subtract.

This is the OAuth standard shape and it matters that it is implemented as standard: scopes become
claims at authentication, and authorization checks **two independent gates** — the role policy and a
scope requirement — both of which must pass. The intersection is not computed anywhere; it **falls out
of composition**. "A scope can only subtract" is therefore a consequence of the structure rather than
a rule anyone has to implement or guard.

What is *not* standard, and was rejected: translating scopes into narrowed **role** claims. That
conflates two axes into one and is where subtle bugs live.

A useful property comes free: the MCP SDK's `AddAuthorizationFilters()` honours `[Authorize]` on tools
*and filters `tools/list`*, so a read-only connection will not even **see** the write tool, rather than
seeing it and being refused.

## The checks that are NOT inherited

**This is the load-bearing section.** An OAuth-authenticated request does not pass through
`CredentialEpochMiddleware`, which is where the app does its per-request fail-closed checks for JWT
callers. Every one must be re-established:

| Check | Consequence of omitting it |
|---|---|
| **User disabled** | A disabled worker's assistant keeps working |
| **Farm suspended** | A suspended farm's assistants keep working |
| **Must-change-password** | A route straight through the #283 gate |
| **Flock scoping** | A worker's assistant reads flocks they are not assigned to — the hazard [#787](https://github.com/mforce/cluckwork/issues/787) is open about |
| **Rate limits** | One misbehaving integration exhausts the farm's capacity. Must key on the **shared** `IFixedWindowCounter` (#543/#544), never a process-local limiter — that is the #271 blocker shape derived wrongly twice |

## The consent screen

The only screen a human sees, and the only moment a security decision is made. Everything else —
registration, token issuance, refresh — happens without a person.

- **All-or-nothing.** With two scopes the choice is nearly meaningless, and partial grants fail in ways
  neither the assistant nor the user explains well.
- **Skipped on re-approval** unless the app asks for *more*. Prompting every time trains people to
  click through a dialog that always says the same thing, which is how consent screens stop working.
- **In the SPA**, not server-rendered. The app ships three locales; a hand-rolled page would need its
  own i18n, styling and accessibility work on the one screen where clarity matters most.
- It must say **"acting as you"** — that the assistant can never exceed the signed-in user. A farmer's
  mental model of "an AI has access to my farm" is likely scarier than the reality.

### The open-redirect hazard

Sending an unauthenticated user to login and then back to consent introduces a classic hole. **The
return URL must be validated against an allow-list of internal paths.** Followed unchecked, an attacker
can send a link that logs someone into Cluckwork and bounces them to a lookalike asking for their
password — and the redirect is invisible to the user.

This is **separate** from OAuth's `redirect_uri` validation, which OpenIddict handles against the
registered client. The hazard is the login → consent hop *inside* the app, which OpenIddict knows
nothing about. The guard must also refuse **protocol-relative** URLs (`//evil.example`), which look
relative and are not — the variant that slips through naive checks.

Test it against a **built** SPA with the service worker active: #778 records this repo's service worker
swallowing server-issued redirects, and this hop goes through the same machinery.
