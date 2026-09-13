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
| **Client registration** | Dynamic self-registration, rate-limited, unapproved apps expire | A deliberate **compatibility** choice — see *Registration: DCR is not the spec's recommendation* below. Registration grants **zero** access, so the risk is junk rows, not unauthorized access |
| **Who may connect** | Any user, for their own data | A Worker's assistant gets a Worker's view. Effective permission is always `role ∩ scope` |
| **Approval** | Re-enter current password | Reuses #308's step-up grant. Stops someone at an unlocked laptop silently connecting an assistant |
| **Scopes** | Two: read farm data, record daily entries | Matches the 7-tool phase-1 surface. A consent screen with eight checkboxes is one people click through without reading |
| **Token format** | Reference tokens | Chosen for revocability, **but the format alone does not deliver it** — see *Revocation is not free* below |
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

## The checks that must be re-established — and who actually owns each

**An earlier draft of this document said OAuth "bypasses `CredentialEpochMiddleware`" and attributed
all of these checks to it. That was wrong in both directions** and is corrected here.
`CredentialEpochMiddleware` inspects **any authenticated principal** and has no
authentication-scheme exemption, so it *will* run for an OAuth caller — and will reject a token that
carries no `credential_epoch` claim. Meanwhile flock resolution runs *before* it and
must-change-password *after* it, so they are separate owners, not one boundary.

The real question is therefore not "which checks are skipped" but **where OAuth populates the
principal, and what claims it supplies**:

| Check | Owner | What OAuth must decide |
|---|---|---|
| **Credential epoch** | `CredentialEpochMiddleware` | Does an OAuth token carry `credential_epoch`? If not, it is rejected outright. If yes, role changes revoke it — which is what makes the role promise below true |
| **User disabled / farm suspended** | Same middleware, one correlated read | Inherited **only if** the principal is populated before it runs |
| **Must-change-password** | `MustChangePasswordMiddleware` | Enforcement depends on the **claim being supplied**, not merely on the middleware executing |
| **Flock scoping** | `FlockScopeResolutionMiddleware`, *before* the epoch check | Requires a resolved user; authenticating OAuth too late misses it entirely |
| **Rate limits** | `UseRateLimiter`, **before** authentication, endpoint opt-in | Not inherited — `/mcp` must opt in, on the shared `IFixedWindowCounter` (#543/#544), never a process-local limiter (#271) |

**The implementable requirement:** authenticate OAuth during `UseAuthentication`, and have the token
produce a principal carrying the same claims a session JWT does — `sub`, `account_id`,
`credential_epoch`, roles. Then the existing chain applies unchanged. Departing from that is
possible but every departure must be justified against this table.

## Role freshness: the promise above is not free

`role ∩ scope` promises the user's **current** authority. Nothing in the pipeline reloads roles:
`TenantResolutionMiddleware` copies them from token claims, and `AuthPolicies.EffectiveRole` reads
`IsInRole`/`FindAll("role")` off the principal. Today that is safe **only because a role change bumps
`CredentialEpoch` and revokes the token** — freshness comes from revocation, not from live reads.

**An earlier draft asserted the opposite** — that roles are read live, so a credential narrows
automatically after a demotion. That is false, and it was the argument used to justify *not* tying
credentials to the epoch. Correcting it reverses that conclusion.

So one of these must be chosen explicitly, and the choice belongs in #796:

1. **Carry `credential_epoch` and let the existing check revoke on role change** — reuses the
   mechanism, costs nothing new, and means a demotion disconnects the assistant (it must reconnect).
2. **Reconstruct roles live** before authorization and flock resolution — the assistant survives a
   demotion with narrowed authority, at the cost of a read and a second freshness mechanism.

Failure mode if neither is done: **a Manager demoted to ReadOnly keeps Manager authority through an
already-issued OAuth token**, passing both gates indefinitely, because the token is valid and the
scope intersection is computed against a stale role.

## Revocation is not free either

Reference tokens were chosen so Disconnect takes effect on the next call. **The format alone does not
establish that.** OpenIddict validates *token entries* for reference tokens, but does **not** check
**authorization-grant** status by default — so revoking the stored authorization can leave its access
tokens usable. `UseLocalServer()` enables token-entry validation, not authorization-entry validation.

#796 must state exactly what Disconnect revokes and what each request validates. If Disconnect
revokes the authorization, `EnableAuthorizationEntryValidation()` is required and issued tokens must
carry the authorization id. This matters more here than it would elsewhere, because lifetimes are
indefinite: a token that outlives its revoked authorization never expires on its own.

Also worth correcting: JWTs were ruled out partly on the grounds that they cannot be revoked.
OpenIddict *can* revoke JWTs through token-entry validation. Reference tokens remain the right choice
for simplicity and because revocation is the default posture, but the stated rationale was too strong.

## Registration: DCR is not the spec's recommendation

The MCP specification (2025-11-25) recommends **Client ID Metadata Documents**; Dynamic Client
Registration is described as *optional*, for backward compatibility or specific requirements. An
earlier draft asserted DCR was the recommendation.

DCR is kept as a deliberate **compatibility** choice, since clients in the field implement it — but
#797 must name the protocol version targeted and decide explicitly whether metadata-document support
is included or deferred. A client implementing only the recommended mechanism would not interoperate
with a DCR-only server.

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
