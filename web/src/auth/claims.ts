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
