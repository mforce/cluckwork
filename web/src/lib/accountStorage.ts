// Per-account browser state (#535). Multi-farm (#530) means one device can hold
// several farms' remembered selections, and an un-namespaced key hands farm A's
// state to farm B.
//
// The namespace is the ACCOUNT GUID the server minted into the access token, not
// the farm code the user typed. bindAccount(accountIdFromToken(...)) runs on every
// path that ends authenticated — login (client.ts:250), change-password
// (client.ts:312) and the cold-restore refresh (client.ts:534) — each with a
// non-null id or a hard refusal, so it is available synchronously from the first
// authenticated render.
//
// A read with NO bound account returns null and deliberately does NOT fall back to
// the bare key. Falling back is precisely how farm B inherits farm A's state.
import { getBoundAccountId } from "../auth/tokenStore";

export function accountScopedKey(base: string): string | null {
  const accountId = getBoundAccountId();
  return accountId === null ? null : `${base}:${accountId}`;
}

export function readAccountScoped(base: string): string | null {
  const key = accountScopedKey(base);
  if (key === null) return null;
  try {
    return localStorage.getItem(key);
  } catch {
    // Site data unavailable (private mode / blocked cookies) — the caller falls
    // back to its own default, exactly as it would with nothing remembered.
    return null;
  }
}

export function writeAccountScoped(base: string, value: string): void {
  const key = accountScopedKey(base);
  if (key === null) return;
  try {
    localStorage.setItem(key, value);
  } catch {
    // Unavailable or quota exhausted. Nothing here is load-bearing: the caller's
    // in-memory state stays authoritative for this page lifetime.
  }
}

// #535 — values written by every build before this one. They cannot be attributed
// to a farm (the app did not know which farm wrote them), so migrating them would
// be a guess; they are dropped instead. Same shape as purgeLegacyTokens(). Costs a
// one-time loss of a remembered flock, re-established on the next Daily Entry visit.
// #586 adds the farm palette. Before per-farm keys existed this held ONE colour
// for the whole device, and which farm it belonged to is unknowable — the device
// may have used several. Read by anything, it paints one farm's colour on
// another's login screen, so it is dropped rather than migrated. Costs a
// default-palette cold start until the next login, which rewrites it per farm.
const UNSCOPED_KEYS = ["cluckwork.lastFlockId", "cluckwork.brand"] as const;

export function purgeUnscopedAccountState(): void {
  for (const key of UNSCOPED_KEYS) {
    try {
      localStorage.removeItem(key);
    } catch {
      // storage unavailable — nothing to purge
    }
  }
}
