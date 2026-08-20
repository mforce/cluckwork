import { getAccessToken } from "./tokenStore";

// #103 — the shipped roles (spec §5.1). "Admin" is the Owner's stored name
// (#73 heritage); a user with no role claim is a plain Worker. Typed here so
// screens never compare raw strings (#84).
export type Role = "Admin" | "Manager" | "Sales" | "ReadOnly" | "Worker" | "Denied";

// Shared by every claim decoder below: the raw payload object, or null for a
// missing/malformed token. Centralized so a token-shape change (or a decode
// bug fix) only needs fixing in one place.
function decodedPayload(): Record<string, unknown> | null {
  const token = getAccessToken();
  if (!token) return null;
  try {
    let payloadPart = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    payloadPart = payloadPart.padEnd(payloadPart.length + ((4 - (payloadPart.length % 4)) % 4), "=");
    return JSON.parse(atob(payloadPart)) as Record<string, unknown>;
  } catch {
    return null;
  }
}

// This decode drives UI visibility only; the API enforces the policy on every
// gated endpoint regardless of what the client shows.
export function currentUserRole(): Role {
  const payload = decodedPayload() as { role?: string | string[] } | null;
  if (!payload) return "Worker";
  const roles = (Array.isArray(payload.role) ? payload.role : [payload.role])
    .filter((r): r is string => typeof r === "string" && r.length > 0);
  for (const r of ["Admin", "Manager", "Sales", "ReadOnly"] as const)
    if (roles.includes(r)) return r;
  // A user carrying only unrecognized role claims is denied, not a worker —
  // Worker means NO role claims (codex review of #104).
  return roles.length > 0 ? "Denied" : "Worker";
}

export function currentUserIsAdmin(): boolean {
  const role = currentUserRole();
  return role === "Admin" || role === "Manager";
}

// #532 — decodes the account claim from an ARBITRARY token string, not the
// stored one. executeRefresh needs to compare the account of the token it is
// about to adopt against the account this tab was already operating as: the
// refresh cookie is per-ORIGIN (one per browser, last login wins) while the
// token store is per-TAB, so a refresh in an old tab can legitimately return a
// different farm's session.
export function accountIdFromToken(token: string | null): string | null {
  if (!token) return null;
  try {
    let payloadPart = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    payloadPart = payloadPart.padEnd(payloadPart.length + ((4 - (payloadPart.length % 4)) % 4), "=");
    const payload = JSON.parse(atob(payloadPart)) as { account_id?: unknown };
    return typeof payload.account_id === "string" && payload.account_id.length > 0
      ? payload.account_id
      : null;
  } catch {
    return null;
  }
}

// #356 (codex review of #492 round 10) — the caller's own id, straight from
// the token's standard "sub" claim, for UI checks that must not depend on
// /me succeeding. SessionProvider deliberately keeps the shell up with
// me === null when /me fails, and a self-target guard written as
// `me?.id !== u.id` then reads true for EVERY row, including the caller's
// own — exposing Disable/Enable on themselves for a submit that can only
// 400. The token is already decoded and present the instant the user is
// authenticated; /me is a separate, failable network call.
export function currentUserId(): string | null {
  const payload = decodedPayload() as { sub?: string } | null;
  return typeof payload?.sub === "string" && payload.sub.length > 0 ? payload.sub : null;
}

// #283 — decodes the "must_change_password" claim JwtTokenService adds ONLY
// when ApplicationUser.MustChangePassword is true (the first-run admin, or
// anyone else whose password was force-reset and hasn't changed it since).
// UI visibility only, same disclaimer as currentUserRole above: the API's
// MustChangePasswordMiddleware enforces this on every request regardless of
// what the SPA shows.
export function currentUserMustChangePassword(): boolean {
  const payload = decodedPayload() as { must_change_password?: string } | null;
  return payload?.must_change_password === "true";
}
