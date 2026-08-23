// #535 — the farm codes that have SUCCESSFULLY signed in on this device.
//
// Deliberately NOT account-namespaced: this list IS the cross-farm roster, so
// keying it per account would be circular — you need it before you know the farm.
//
// Accepted disclosure (epic #530 decision 9, recorded in #537's ADR): on a shared
// device this is a durable roster of which farms this browser profile uses.
const KEY = "cluckwork.farmCodes";

// Mirrors Account.SlugPattern (src/Cluckwork.Domain/Accounts/Account.cs:36):
// 3-32 chars, lowercase alnum + hyphen, no leading or trailing hyphen. ANCHORED,
// so an over-long value FAILS rather than being truncated — #535 requires that an
// over-long value is ignored, not shortened.
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

export function rememberFarmCode(value: string): void {
  const code = canonicalFarmCode(value);
  if (code === null) return;
  const next = [code, ...readFarmCodes().filter((c) => c !== code)].slice(0, MAX_REMEMBERED);
  try {
    localStorage.setItem(KEY, JSON.stringify(next));
  } catch {
    // Unavailable or full. The sign-in already succeeded; the only cost is that
    // this device does not offer the code next time.
  }
}
