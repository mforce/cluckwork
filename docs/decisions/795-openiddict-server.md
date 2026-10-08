# Run OpenIddict outside Production only, on the shared Data Protection ring (#795)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break). The design it implements is in
> [`docs/plans/788-mcp-oauth/`](../plans/788-mcp-oauth/00-README.md).

**Status:** accepted
**Date:** 2026-10-08

## What happened

No incident. This is a forward-looking choice. #795 stands up OpenIddict as an OAuth 2.1
authorization server for third-party clients: the authorization-code flow with PKCE, and
reference access tokens that live until revoked (#788). The checks that make an OAuth
token safe on a business endpoint (#796), client registration (#797) and consent with
step-up (#798) do not exist yet. This slice therefore has to issue and validate tokens
without letting one reach a business endpoint, and without letting Production issue one.

## The rule

`AddCluckworkIdentity` registers OpenIddict only for a serving process outside
Production with `OAuth:Issuer` set. Production registers no OpenIddict service and maps
no OAuth endpoint, whatever its configuration says. Codes and access tokens use the Data
Protection format on the shared key ring (#794). OpenIddict's mandatory signing and
encryption keys are ephemeral, and the `openid` scope and the key-set endpoint are
removed, so nothing is ever signed with them. Business endpoints authenticate with the
session JWT scheme only, and an issued token names its user and nothing else. PKCE accepts
S256 only, because a `plain` challenge is the verifier itself.

## The key story

The 2026-09-13 comment on #795 priced two options: two RSA certificates, or the Data
Protection token format. Checked against the OpenIddict 7.7.1 source:

- **OpenIddict refuses to start without both keys.** `OpenIddictServerConfiguration`
  throws `ID0085` without an encryption key and `ID0086` without an asymmetric signing
  key. Neither check looks at the token format.
- **The Data Protection integration covers every token this server issues.** Access
  tokens, authorization codes, refresh tokens, device codes, user codes and request
  tokens all go through the key ring unless a `PreferDefault*Format` flag is set. Only
  identity tokens use the keys.
- **`openid` is the way in.** It is in `OpenIddictServerOptions.Scopes` by default, and
  `AttachDefaultScopes` grants it whenever a client asks and the sign-in principal names
  no scopes. The token response then carries an identity token signed with the key.
  `OpenIdScope_IsRefused` caught this: the first version of this slice issued one.

So the keys are ephemeral, `openid` is removed from the registered scopes, and the
key-set endpoint is unmapped, because it would publish a different key on every replica.
The Data Protection format adds no configuration key: the ring #794 already requires in
Production is the one that protects these tokens. `CodeAndToken_CrossReplicas_ThroughTheSharedKeyRing`
issues a code on one host and redeems it on another with different ephemeral keys; it
passes only because both read the same ring.

## Logs

OpenIddict logs whole protocol messages at Information: the authorization, token,
revocation and introspection requests and every response it writes. Its
`OpenIddictMessage.ToString()` redacts codes, tokens, client secrets and passwords, but
not `code_verifier`, so each token request put the verifier in the log. Review of this
PR found that in the live run's output. At Warning and above OpenIddict logs only row
ids, key type names and exceptions. `AddCluckworkTelemetry` therefore filters out every
event below Warning whose source is `OpenIddict` or a category under it, before any
sink. A filter, because an override cannot hold the floor: Serilog applies the most
specific override, so a configured `OpenIddict.Server` override beat the first
version's parent clamp (review round 2). No override or configuration reload changes
the filter. A property-name rule could not help either, because the verifier sits
inside the rendered message, and a content pattern would miss the next field OpenIddict
leaves unredacted. The cost is that rejection reasons no longer reach the log; OpenIddict
still returns them to the client as `error_description`.
`ProtocolSecrets_NeverReachTheLog` sets `OpenIddict` and `OpenIddict.Server` to Verbose
through configuration, runs a redeemed and a refused exchange with marker values, and
fails if any captured event carries one.

## Why not the obvious alternative

- **Two RSA certificates.** Two new required Production keys, a rotation story beside
  #510's and #794's, and #370/#565 work, for keys that would sign nothing.
- **Registering the server in Production behind a refusing consent stub.** Production
  would then need `OAuth:Issuer` now, and would expose token and discovery endpoints that
  can never succeed. Not registering it leaves Production's surface unchanged.
- **Requiring `OAuth:Issuer` outside Production.** Many test hosts and subprocess tests
  boot Development or Testing without it. An unset issuer simply leaves the server off;
  `appsettings.Development.json` and the test factory set one.
- **Deriving the issuer from the request.** Discovery would vary with the Host header
  (#538).

## Two walls between an OAuth token and a business endpoint

1. **Authentication.** The default scheme stays JWT bearer. A reference token is not a
   JWT, so it fails authentication on every business endpoint before any middleware reads
   a claim (`OAuthToken_IsRejectedByBusinessEndpoints_AtAuthentication`).
2. **Claims.** The authorization endpoint puts only `sub` in the token. Routed through
   the default scheme anyway, the principal has no `account_id`, so
   `TenantResolutionMiddleware` returns 401. With an `account_id` and no
   `credential_epoch`, `CredentialEpochMiddleware` reads epoch 0, which never matches
   (#364); its own tests cover a principal without the claim from any scheme
   (`OAuthToken_ForcedThroughTheDefaultScheme_IsStillRejected`).

#796 replaced the second wall with the real checks and made the first per-endpoint; see
[`796-oauth-fail-closed.md`](796-oauth-fail-closed.md). The wall-2 test went with it.

## What this does NOT cover

- **Consent.** A caller signed in with a session JWT approves its own request. That is
  the stub #795's "done when" allows, and Production cannot reach it.
- **Scopes.** None are registered. #796 and #798 add the two from #788.
- **Revocation and pruning.** Nothing revokes or prunes tokens yet. #796 decides what
  Disconnect revokes; #799 adds the screens.
- **Rate limits** on the OAuth endpoints (#796).

## How it is enforced

- `OAuthServerProductionTests` boots Production with an issuer configured and asserts
  the token, authorization and metadata endpoints answer 404 and no OpenIddict manager
  resolves.
- `OAuthServerTests` runs the flow end to end against a test-only resource endpoint and
  pins the reference token, its missing expiry, the PKCE requirement, the `openid`
  refusal, the cross-replica redemption and both walls.
- `tools/oauth/mutation-check.sh` applies one mutation per claim, rebuilds, and requires the
  named test to fail.
- `TableOwnerRealModelTests`, `TenantBypassDiscoveryTests` and `BusinessRecordModelTests`
  name the four OpenIddict tables explicitly.
