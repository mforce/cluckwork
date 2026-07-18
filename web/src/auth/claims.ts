import { loadTokens } from "./tokenStore";

// #73 — the API emits a short "role" claim (one per role) in the access token.
// This decode drives UI visibility only; the API enforces the policy on every
// gated endpoint regardless of what the client shows.
export function currentUserIsAdmin(): boolean {
  const tokens = loadTokens();
  if (!tokens) return false;
  try {
    const payloadPart = tokens.accessToken.split(".")[1];
    const json = atob(payloadPart.replace(/-/g, "+").replace(/_/g, "/"));
    const payload = JSON.parse(json) as { role?: string | string[] };
    const roles = Array.isArray(payload.role) ? payload.role : [payload.role];
    return roles.includes("Admin");
  } catch {
    return false;
  }
}
