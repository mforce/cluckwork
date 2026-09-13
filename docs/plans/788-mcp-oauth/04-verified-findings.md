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

## 4. Data Protection has no persisted key ring — a latent defect found in passing.

No `AddDataProtection()`, no `PersistKeysTo*`, no key-ring configuration anywhere. Meanwhile
`AddDefaultTokenProviders()` **is** registered, which wires a data-protector-backed token provider, and
`GeneratePasswordResetTokenAsync` is called today.

It only works because the token is minted and consumed in the **same request**. A real forgot-password
flow, or a second replica, breaks it. It fails **closed**, so nothing is insecure — it simply stops
working. Filed independently as [#794](https://github.com/mforce/cluckwork/issues/794), and it is a
prerequisite for [#795](https://github.com/mforce/cluckwork/issues/795).

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
