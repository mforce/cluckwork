import { loadTokens } from "./tokenStore";

// #103 — the shipped roles (spec §5.1). "Admin" is the Owner's stored name
// (#73 heritage); a user with no role claim is a plain Worker. Typed here so
// screens never compare raw strings (#84).
export type Role = "Admin" | "Manager" | "Sales" | "ReadOnly" | "Worker" | "Denied";

// This decode drives UI visibility only; the API enforces the policy on every
// gated endpoint regardless of what the client shows.
export function currentUserRole(): Role {
  const tokens = loadTokens();
  if (!tokens) return "Worker";
  try {
    let payloadPart = tokens.accessToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    payloadPart = payloadPart.padEnd(payloadPart.length + ((4 - (payloadPart.length % 4)) % 4), "=");
    const payload = JSON.parse(atob(payloadPart)) as { role?: string | string[] };
    const roles = (Array.isArray(payload.role) ? payload.role : [payload.role])
      .filter((r): r is string => typeof r === "string" && r.length > 0);
    for (const r of ["Admin", "Manager", "Sales", "ReadOnly"] as const)
      if (roles.includes(r)) return r;
    // A user carrying only unrecognized role claims is denied, not a worker —
    // Worker means NO role claims (codex review of #104).
    return roles.length > 0 ? "Denied" : "Worker";
  } catch {
    return "Worker";
  }
}

export function currentUserIsAdmin(): boolean {
  const role = currentUserRole();
  return role === "Admin" || role === "Manager";
}
