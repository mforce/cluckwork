# Libraries evaluated (#788)

Recorded so nobody re-researches this. All verified against live sources, not recalled.

| Package | Alive? | Licence | .NET 10 | Verdict |
|---|---|---|---|---|
| **OpenIddict** 7.7.0 | Yes — sponsored, 6 open issues, 0 open PRs | **Apache-2.0** | Confirmed on NuGet | **Chosen.** Auth code + PKCE, client credentials, device flow, discovery metadata |
| **Duende IdentityServer** | Yes, commercial | Source-available, paid in production | Not verified | **Rejected on price** — $5,750/yr (Lite, 2 client IDs) to $24,900/yr |
| **Fido2.NET** | Yes — .NET Foundation, 34 open issues | MIT | .NET 8+ | **Keep for [#320](https://github.com/mforce/cluckwork/issues/320).** Still recommended *alongside* Identity 10's passkeys for attestation, which the in-box implementation does not do |
| **Otp.NET** | Stable, low churn | MIT | Not confirmed | **Not needed** — Identity 10 ships `AuthenticatorTokenProvider` in-box |
| **QRCoder** | Yes — **maintainer changed to Shane32 in 2025** | MIT, zero deps | net5.0+ claimed | **Only if** we render TOTP enrolment QR codes ourselves |
| **AspNetCore.Authentication.ApiKey** | Alive, slow cadence | MIT | **No .NET 10 release visible** | **Skip** — saves ~100 lines; every dependency is advisory surface under #146 |

## What ASP.NET Core 10 gives for free

Verified against the ref-pack assemblies at `Microsoft.AspNetCore.App.Ref/10.0.12`. **All already on
this repo's compile graph with zero new package references** — Identity is in-box shared framework for
`net10.0` and both projects carry `FrameworkReference Microsoft.AspNetCore.App`. The #684/#146
packaging tax for any of it is **zero**.

- **TOTP**: `AuthenticatorTokenProvider<TUser>`, `GenerateNewAuthenticatorKey`,
  `VerifyTwoFactorTokenAsync`, `GenerateNewTwoFactorRecoveryCodesAsync` — recovery codes included.
- **Passkeys**: `IUserPasskeyStore<TUser>`, `UserPasskeyInfo`, `IdentityPasskeyOptions`,
  `IPasskeyHandler<TUser>` — but a **primary** factor by design (*"No built-in 2FA support"*), and the
  entry points are on `SignInManager`, which this repo does not register.
- **`Microsoft.AspNetCore.Authentication.BearerToken`** and **`MapIdentityApi<TUser>()`** — present but
  unmounted, and unsuitable here.

This repo calls **none** of it. `TwoFactorEnabled` exists only as an unused EF column.

## The rejected PAT design, kept for reference

If OAuth ever proves too heavy, these conclusions are reusable rather than needing rediscovery:

- Self-service on the **Account page** (`AccountPage.tsx`, where change-password lives) — *not* the
  Owner-only `UsersPage.tsx`, which manages other people.
- **Opaque** 256-bit random, SHA-256 hash stored, raw never persisted — mirroring `RefreshToken`.
- **`cw_pat_` prefix + CRC32/Base62 checksum**, following GitHub's convention: detectable by secret
  scanners, and typos rejected with no database lookup.
- **Not** revoked by password or role change. Because roles are read live, a demoted user's credential
  narrows automatically. GitHub states the principle: *"A token cannot grant additional access
  capabilities to a user."*
- Revoked by explicit revocation, expiry, user disable, and account suspension.
- Mandatory expiry, user-chosen and capped; no "never".
- Owner gets farm-wide visibility and revocation.
