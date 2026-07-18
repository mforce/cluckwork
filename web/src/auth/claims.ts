import { loadTokens } from "./tokenStore";

// #73 — the API emits a short "role" claim (one per role) in the access token.
// This decode drives UI visibility only; the API enforces the policy on every
// gated endpoint regardless of what the client shows.
export function currentUserIsAdmin(): boolean {
  const tokens = loadTokens();
  if (!tokens) return false;
  try {
    let payloadPart = tokens.accessToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    payloadPart = payloadPart.padEnd(payloadPart.length + ((4 - (payloadPart.length % 4)) % 4), "=");
    const payload = JSON.parse(atob(payloadPart)) as { role?: string | string[] };
    const roles = Array.isArray(payload.role) ? payload.role : [payload.role];
    return roles.includes("Admin");
  } catch {
    return false;
  }
}
