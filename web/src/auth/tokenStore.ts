// #145 — the access token lives ONLY in JS memory, never in localStorage/
// sessionStorage. An XSS payload therefore can't read a durable credential; the
// token's short (15-min) lifetime bounds the exposure. The refresh token is an
// HttpOnly cookie the browser attaches automatically and JS cannot read.
//
// A page reload clears this module's memory, so the session is restored by a
// silent refresh against the cookie (see client.restoreSession / AuthContext).

// Pre-#145 sessions persisted the token pair here; purged on first load so those
// users cleanly re-login (a stale access token is never trusted from storage).
const LEGACY_KEY = "cluckwork.tokens";

let accessToken: string | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

export function clearAccessToken(): void {
  accessToken = null;
}

// #532 — the farm this TAB is bound to, tracked separately from the access
// token and deliberately NOT cleared by clearAccessToken(). It is a non-secret
// selector, persisted in sessionStorage so this tab can name its farm after a
// reload when the browser holds several farms' HttpOnly cookies. sessionStorage
// is tab-scoped and disappears when the tab closes; the access token remains
// memory-only.
//
// The cross-farm guard in client.ts compares a refreshed token's account
// against this. Deriving it from the stored token instead makes the guard a
// one-shot: refusing a foreign token tears the session down, clearing the
// token, and the next refresh then sees an empty store — the same state as a
// legitimate cold restore — and adopts the foreign farm with no comparison.
// Round 7 reproduced exactly that with two concurrent requests.
//
// Cleared only by an explicit logout (the user chose to end the session). A
// genuinely fresh tab has no binding and uses restoreSession's unbound
// bootstrap path.
const BOUND_ACCOUNT_KEY = "cluckwork.boundAccountId";

function readBoundAccount(): string | null {
  try {
    return sessionStorage.getItem(BOUND_ACCOUNT_KEY);
  } catch {
    // Site data is unavailable — keep this tab usable with memory-only state.
    return null;
  }
}

function persistBoundAccount(accountId: string | null): void {
  try {
    if (accountId === null) sessionStorage.removeItem(BOUND_ACCOUNT_KEY);
    else sessionStorage.setItem(BOUND_ACCOUNT_KEY, accountId);
  } catch {
    // The in-memory binding remains authoritative for this page lifetime.
  }
}

let boundAccountId: string | null = readBoundAccount();

export function getBoundAccountId(): string | null {
  return boundAccountId;
}

export function bindAccount(accountId: string | null): void {
  boundAccountId = accountId;
  persistBoundAccount(accountId);
}

export function clearBoundAccount(): void {
  bindAccount(null);
  // #586 — the farm binding goes with the account binding. Note this is NOT done
  // inside bindAccount: that runs on every refresh (client.ts:534) with the same
  // account, and clearing the slug there would blank the cache every 15 minutes.
  bindFarm(null);
}

// #586 — the farm code this TAB's login typed, stored WITH the account it was
// proven against. ONE record rather than two keys, because the pair must never
// desync: a slug outliving its account binding would key another farm's palette
// under this farm's code, which is the exact leak #586 exists to close.
// getBoundFarmCode() enforces the pairing with a comparison; nothing here relies
// on a comment, or on client.ts's rebind guard staying as it is.
//
// sessionStorage, tab-scoped, and NOT cached in module memory — every read goes
// to storage, so a record written by another code path in the same tab is seen
// immediately. A fresh tab restoring from the refresh cookie has no record,
// which is why applyBrand caches nothing there (design D2).
const BOUND_FARM_KEY = "cluckwork.boundFarm";

interface BoundFarm {
  accountId: string;
  slug: string;
}

function readBoundFarm(): BoundFarm | null {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(BOUND_FARM_KEY);
  } catch {
    // Site data unavailable — behave as an unbound tab.
    return null;
  }
  if (raw === null) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  // BOTH checks are load-bearing: destructuring a parsed `null` throws, and a
  // parsed string would silently destructure to undefined fields.
  if (typeof parsed !== "object" || parsed === null) return null;
  const { accountId, slug } = parsed as Partial<BoundFarm>;
  if (typeof accountId !== "string" || typeof slug !== "string") return null;
  return { accountId, slug };
}

// Pass null to clear. A slug with no account to pin it to is exactly the desync
// this record exists to prevent, so that case clears rather than storing half a
// pair — an unattributable token makes client.ts:250 bind a null account.
export function bindFarm(slug: string | null): void {
  const accountId = getBoundAccountId();
  if (slug === null || accountId === null) {
    try {
      sessionStorage.removeItem(BOUND_FARM_KEY);
    } catch {
      // storage unavailable — nothing to clear
    }
    return;
  }
  try {
    sessionStorage.setItem(BOUND_FARM_KEY, JSON.stringify({ accountId, slug }));
  } catch {
    // The palette cache is a pre-paint hint; losing it costs one flash, never
    // correctness.
  }
}

export function getBoundFarmCode(): string | null {
  const record = readBoundFarm();
  if (record === null) return null;
  const accountId = getBoundAccountId();
  // ONE comparison, deliberately, not two. `record.accountId` is already proven
  // to be a STRING by readBoundFarm, and a string is never `=== null` — so this
  // same line is what rejects an unbound tab. An extra `accountId === null ||`
  // clause would be a redundant clause that READS as an extra guarantee, which is the defect
  // class this whole issue exists to remove. It was in the first draft; two
  // reviewers independently proved no mutation could redden it.
  if (record.accountId !== accountId) return null;
  return record.slug;
}

// Remove any token left in localStorage by the pre-#145 scheme. Called once at
// startup; safe to call when storage is unavailable (private mode).
export function purgeLegacyTokens(): void {
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // storage unavailable — nothing to purge
  }
}
