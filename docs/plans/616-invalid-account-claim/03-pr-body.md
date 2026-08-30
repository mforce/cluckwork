Closes #616.

## What changed

- reject authenticated principals whose required `account_id` claim is missing or malformed before tenant, user, or flock scope resolution
- preserve the existing valid-claim and anonymous health paths
- add direct middleware regression coverage for both invalid account forms, invalid `sub`, valid Tenant → Flock composition, and anonymous database independence
- correct the report limiter's now-stale unresolved-tenant comment

## Verification

- RED reproduced for missing and malformed `account_id`: expected `(401, 0, false, false)`, actual `(200, 1, false, true)`
- focused middleware suite
- full .NET build and test suite
- causal mutation checks for missing, malformed, valid, anonymous, invalid-`sub`, and Tenant → Flock order boundaries
- repository CI gates

The current later credential-epoch gate and tenant query filters already prevent tenant data exposure; this fix closes the earlier fail-open intermediate state and unwanted flock-scope work.
