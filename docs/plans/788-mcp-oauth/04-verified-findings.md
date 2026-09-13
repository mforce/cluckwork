# Verified findings (#788)

Facts checked against source, the restored assemblies, or live vendor documentation. Four overturned
assumptions made earlier in the same design conversation — recorded because the corrections are the
part a later reader cannot reconstruct.

## 1. TOTP is in-box. The earlier claim that it was greenfield was wrong.

An earlier round of this design advised against adding TOTP on the grounds that it meant "a whole
subsystem — enrolment, secret storage, QR, recovery codes." Verified false: ASP.NET Core Identity
10.0.12 ships `AuthenticatorTokenProvider<TUser>`, `GenerateNewAuthenticatorKey`,
`VerifyTwoFactorTokenAsync` and `GenerateNewTwoFactorRecoveryCodesAsync`. The remaining work is an
enrolment UI and a verify endpoint. Recorded on
[#320](https://github.com/mforce/cluckwork/issues/320).

## 2. Passkeys ship in .NET 10, but not as a second factor.

Confirmed present and first-class. But Microsoft's documentation states verbatim: *"No built-in 2FA
support: Passkeys are treated as a primary authentication factor, not as a second factor."*
[#320](https://github.com/mforce/cluckwork/issues/320) wants them as a **step-up** factor, so that
slice is *more* work than its body implies, not less — adapting a primary-factor implementation into a
second-factor grant is the actual task. Compounding it: the entry points are on `SignInManager`, and
this repo uses `AddIdentityCore` **without** `AddSignInManager()`, so login is hand-rolled and
`SignInManager` is never registered.

## 3. Adding 4 tables breaks exactly one guard, and it is the right one.

The tenancy stack **skips** entities without `AccountId` structurally rather than demanding one:

- The #673 model walk does `if (accountId is null) continue;` — it throws only for a *mapped*
  `AccountId` of the wrong CLR type, never for absence.
- `TenantStampInterceptor` does the same: `if (prop is null) continue;`.
- Query filters are hand-written per type, not applied by a walk.
- The #613 flock walk requires a `FlockId` property.

**So the boot is unaffected.** One test fails: `TenantBypassDiscoveryTests.DiscoveredSurface_Floor`
asserts **exact set equality** over filter-free entities, so the 4 new tables turn it red until someone
adds them deliberately with a reason. That is the guard working as designed — a registry that must be
consciously edited, not a wall to route around. **Do not relax it to a subset check.**

## 4. Data Protection has no *explicit* key-ring configuration — but the failure boundary is narrower than first stated

No `AddDataProtection()`, no `PersistKeysTo*`, no key-ring configuration anywhere. `AddDefaultTokenProviders()`
**is** registered, and `GeneratePasswordResetTokenAsync` is called today.

**An earlier draft overstated the consequence**, claiming a second replica or a later redemption
would break it. Corrected: ASP.NET Core Data Protection persists keys **by default** in an
environment-dependent location, and even an ephemeral ring survives across requests **within one
process**. `src/Cluckwork.Api/Dockerfile` already acknowledges default keys under the application
user's home.

The accurate risks are narrower and still real:

- **Container replacement loses an unmounted ring**, invalidating anything protected with it.
- **A second instance cannot consume a token another instance protected** without a compatible shared
  ring.
- Generation and consumption are currently **adjacent calls** in the same operation
  (`IdentityProvider`, including the CLI recovery path), which is why nothing fails today — not
  because keys are somehow durable.

So the claim is: **explicit durable, shared configuration is absent**, and two planned features (#320
TOTP, a real forgot-password flow) would make that absence matter. #794 is reframed accordingly.

## 4b. Roles are NOT read live — and an earlier claim in this very design said they were

`TenantResolutionMiddleware` copies roles from **token claims**
(`context.User.FindAll("role")`), and `AuthPolicies.EffectiveRole` reads `IsInRole`/`FindAll("role")`
off the principal. **Nothing reloads roles from the database on a request.**

An earlier round of this design asserted the opposite — that because a credential is opaque and
database-validated, roles are read live and a demoted user's credential narrows automatically. That
assertion was used to argue *against* tying credentials to `CredentialEpoch`. It is false.

What actually keeps roles fresh today is **revocation**: a role change bumps `CredentialEpoch`, and
the next request with the old token is rejected. Freshness is a property of the revocation mechanism,
not of how the credential is stored.

Consequence for this design: an OAuth token carrying role claims keeps **stale** authority after a
demotion unless it either carries `credential_epoch` (so the existing check revokes it) or roles are
reconstructed live. `02-design.md` now records both options; #796 must pick one.

## 5. Microsoft states there is no PAT primitive.

The Identity API's bearer tokens are documented verbatim as *"not intended to be a full-featured
identity service provider or token server"* — a short-lived-access plus refresh **session** pattern.
No named tokens, no per-token scopes, no independent revocation. This is what settled build-versus-buy:
a PAT would have been entirely hand-rolled, and so would an OAuth server.

## 6. GitHub PATs are not revoked by a password change.

Inferred from an absence in an otherwise exhaustive revocation-trigger list, so stated at that
strength — *very likely, not directly confirmed*. The affirmative principle **is** documented: *"A
token cannot grant additional access capabilities to a user."* Effective authority tracks the user's
live permissions rather than a snapshot. This independently matched the conclusion reached here about
live role reads, and is why the rejected PAT design did not revoke on role change.
