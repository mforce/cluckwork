Closes #616.

## What changed

- reject authenticated principals whose required `account_id` claim is missing or malformed before tenant, user, or flock scope resolution
- preserve the existing valid-claim and anonymous health paths
- add direct middleware regression coverage for both invalid account forms, invalid `sub`, valid Tenant → Flock composition, and anonymous database independence
- correct the report limiter's now-stale unresolved-tenant comment
- for an unmintable external token with valid `sub` but missing/malformed `account_id`, deliberately change the 401 from CredentialEpoch's `Auth.CredentialsSuperseded` ProblemDetails to a bodiless response; existing `Login.tsx` and `UsersPage.tsx` problem-title consumers need no code change

## Verification

- baseline pre-fix reproduction at `1690db89`: expected `(401, 0)`, actual `(200, 1)`
- historical M1/M2 4-tuple mutation RED at `1415f9f3`, before the additive bare-response assertions: expected `(401, 0, false, false)`, actual `(200, 1, false, true)`
- six-element final-harness M1/M2 mutation RED at `0455019`: expected `(401, 0, false, false, 0, null)`, actual `(200, 1, false, true, 0, null)`
- focused middleware suite: 8/8
- full .NET build and test suite: 2080/2080 (361 Domain + 175 Application + 10 AppHost + 1534 API)
- causal M1–M11 mutation checks, including bare-response and real Tenant→Flock/database-attempt boundaries
- caller review: CredentialEpoch's pre-fix ProblemDetails fallback and the existing `Login.tsx`/`UsersPage.tsx` title consumers were inspected; no SPA/localization code change is required
- repository CI gates

The current later credential-epoch gate and tenant query filters already prevent tenant data exposure; this fix closes the earlier fail-open intermediate state and unwanted flock-scope work.
