// #535 — the farm codes that have SUCCESSFULLY signed in on this device.
//
// Deliberately NOT account-namespaced: this list IS the cross-farm roster, so
// keying it per account would be circular — you need it before you know the farm.
//
// Accepted disclosure (epic #530 decision 9, the ADR for which is pending in
// #537): on a shared device this is a durable roster of which farms this browser
// profile uses.
const KEY = "cluckwork.farmCodes";

// An independent COPY of Account.SlugPattern (src/Cluckwork.Domain/Accounts/
// Account.cs:36) in a different language — nothing here enforces that the two
// stay in sync. Drift is fail-safe in the security sense: neither direction can
// make an INVALID code acceptable. But the two directions cost very differently.
// (a) JS looser than the server: an over-permissive cached or URL value reaches
// apiLogin and the server rejects it — a rejected-at-login code. (b) JS stricter
// than the server: canonicalFarmCode returns null for a genuinely valid slug
// that has just signed in successfully, so rememberFarmCode silently declines to
// store it and prefill/picker drop it permanently. The login itself never fails;
// the cost is a silently unofferable valid farm code. The strict direction is the
// one to know about, because it fails SILENTLY. Shape: 3-32 chars, lowercase
// alnum + hyphen, no leading or trailing hyphen. ANCHORED, so an over-long value
// FAILS rather than being truncated — #535 requires that an over-long value is
// ignored, not shortened.
//
// Deliberately does NOT mirror Account.ReservedSlugs. That list lives server-side
// and a copy here would drift; a reserved code is rejected at login with
// Auth.UnknownFarmCode (AuthEndpoints.cs:203) and so can never enter a
// success-only cache. This module's claim is bounded to SHAPE, not acceptability.
const FARM_CODE_PATTERN = /^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$/;

// At most this many codes are kept, most-recent-first.
const MAX_REMEMBERED = 10;

// The server folds case and trims before the lookup — AccountRepository.cs:44 is
// (slug ?? "").Trim().ToLowerInvariant() — and LoginRequestValidator deliberately
// enforces NO pattern, so "Sunny-Acres" and " sunny-acres " both sign in
// successfully. Normalising BEFORE validating is what stops those logins being
// silently un-remembered: without it the picker never appears for anyone whose
// keyboard capitalised the first letter, with no error and a green test suite.
export function normalizeFarmCode(value: string): string {
  return value.trim().toLowerCase();
}

export function isFarmCode(value: string): boolean {
  return FARM_CODE_PATTERN.test(value);
}

// Normalise, then validate. Returns the canonical code, or null when the value is
// not a farm code at all. `unknown` because it also screens values parsed back out
// of localStorage, which anything on this origin can have edited.
export function canonicalFarmCode(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const normalized = normalizeFarmCode(value);
  return isFarmCode(normalized) ? normalized : null;
}

export function readFarmCodes(): string[] {
  let raw: string | null;
  try {
    raw = localStorage.getItem(KEY);
  } catch {
    return [];
  }
  if (raw === null) return [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) return [];
  const seen = new Set<string>();
  const codes: string[] = [];
  for (const entry of parsed) {
    // Re-validated on READ, not only on write (#535 requires it): the value
    // reaches a form field, and localStorage is editable by anything on this origin.
    const code = canonicalFarmCode(entry);
    if (code === null || seen.has(code)) continue;
    seen.add(code);
    codes.push(code);
    if (codes.length === MAX_REMEMBERED) break;
  }
  return codes;
}

// #535 (codex P2) — the roster is a read-modify-write across two separate
// localStorage calls, and multi-farm sessions coexist across tabs BY DESIGN.
// Two tabs completing logins for different farms concurrently both read the same
// array and both write their own: the second setItem wins and the first farm's
// code is silently dropped, on exactly the device this feature exists for.
//
// Serialised with the Web Locks API — the same mechanism and the same
// degradation stance client.ts:402 uses for the refresh cookie. Browsers without
// navigator.locks (older Safari, insecure origins) fall back to the
// unsynchronised read-modify-write: no cross-tab guarantee, but never worse than
// before this change. Unlike client.ts:402 we do NOT pair it with a timeout
// (REFRESH_TIMEOUT_MS): its critical section awaits fetch (unbounded), whereas
// ours is two synchronous localStorage calls with no await, so the lock is held
// for a single task and a tab cannot be suspended mid-hold.
//
// NEVER REJECTS, deliberately. AuthContext.login awaits it AFTER apiLogin has
// already succeeded, so a storage or lock failure must not turn a completed
// sign-in into a thrown login.
const ROSTER_LOCK = "cluckwork.farmCodes.write";

export async function rememberFarmCode(value: string): Promise<void> {
  const code = canonicalFarmCode(value);
  if (code === null) return;
  // Re-reads INSIDE the lock on purpose: a roster read taken before the lock was
  // acquired is exactly the stale value this exists to prevent.
  const write = (): void => {
    const next = [code, ...readFarmCodes().filter((c) => c !== code)].slice(0, MAX_REMEMBERED);
    try {
      localStorage.setItem(KEY, JSON.stringify(next));
    } catch {
      // Unavailable or full. The sign-in already succeeded; the only cost is that
      // this device does not offer the code next time.
    }
  };
  const locks: LockManager | undefined = globalThis.navigator?.locks;
  if (locks === undefined) {
    write();
    return;
  }
  try {
    await locks.request(ROSTER_LOCK, write);
  } catch {
    // A lock we cannot acquire must not cost the write outright. `write` is a
    // re-read-then-rewrite, so running it again here is harmless even in the
    // narrow case where the rejection arrived after it had already run.
    write();
  }
}

// Reads the raw roster, distinguishing "storage is unreadable" (null) from
// "stored but malformed" ("" — JSON.parse of that fails and the roster is
// empty). removeFarmCode must tell the two apart: a removal that cannot READ
// the roster must be a no-op, because an empty-array write on a read failure
// would erase codes it never saw.
function readRawRoster(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

// #587 — the roster's only user-facing exit. Same protocol and the same
// never-rejects contract as rememberFarmCode: a forgotten local convenience
// must never surface as an error on the login screen. A successfully acquired
// Web Lock orders a forget with a concurrent sign-in; the no-lock/rejected-lock
// fallback is, like rememberFarmCode's, a deliberately unsynchronised
// best-effort read-modify-write with no cross-tab ordering promise.
//
// A failed READ is a no-op, not an empty-array write: getItem can throw while
// setItem would still succeed (a quota-limited or partially-broken storage),
// and writing "[]" in that case would destroy every remembered code this call
// never saw. Malformed stored JSON still behaves as an empty roster — only a
// storage-read FAILURE changes the outcome.
export async function removeFarmCode(value: string): Promise<void> {
  const code = canonicalFarmCode(value);
  if (code === null) return;
  // Re-reads INSIDE the lock on purpose: the same stale-read the lock exists
  // to prevent on the write path applies to a removal — a sign-in landing
  // after the lock is acquired must survive a forget issued before it.
  const write = (): void => {
    const raw = readRawRoster();
    if (raw === null) return;
    // Readable-but-malformed stored JSON and non-array JSON parse to an EMPTY
    // roster (mirrors readFarmCodes), and the removal then writes "[]" — the
    // same immediate normalisation readFarmCodes's write path (rememberFarmCode)
    // produces. Only a storage-read FAILURE (readRawRoster === null) is a
    // no-op; a readable value is never left corrupt.
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      parsed = null;
    }
    // Normalise the readable raw roster EXACTLY like readFarmCodes does —
    // canonicalise, dedupe in first-seen order, cap at 10 — and only THEN
    // remove. Capping before deduping (the old shape) would let a hand-written
    // roster of ten farm-a variants push farm-b past the cap, and forgetting
    // farm-a would then erase farm-b too: the removal would rewrite a roster
    // the read path does not even show.
    const roster = Array.isArray(parsed)
      ? (() => {
          const seen = new Set<string>();
          const codes: string[] = [];
          for (const entry of parsed as unknown[]) {
            const code = canonicalFarmCode(entry);
            if (code === null || seen.has(code)) continue;
            seen.add(code);
            codes.push(code);
            if (codes.length === MAX_REMEMBERED) break;
          }
          return codes;
        })()
      : [];
    const next = roster.filter((candidate) => candidate !== code);
    try {
      localStorage.setItem(KEY, JSON.stringify(next));
    } catch {
      // A forgotten local convenience must never surface as an auth failure.
    }
  };
  const locks: LockManager | undefined = globalThis.navigator?.locks;
  if (locks === undefined) {
    write();
    return;
  }
  try {
    await locks.request(ROSTER_LOCK, write);
  } catch {
    // Same rationale as rememberFarmCode: `write` re-reads before it writes,
    // so the fallback run is harmless even after a late rejection.
    write();
  }
}
