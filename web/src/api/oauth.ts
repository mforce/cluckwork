import { apiFetch, STEP_UP_HEADER } from "./client";

// #798 — the authorize endpoint answers the SPA's consent route with JSON:
// what the screen shows, or where to send the browser next.
export interface ConsentRequest {
  clientId: string;
  clientName: string | null;
  // #1148 — the host of the app's metadata document, which Cluckwork fetched itself.
  // Null for an app that registered itself, whose details nobody checked.
  verifiedDomain: string | null;
  redirectHost: string;
  scopes: string[];
  alreadyAllowed: string[];
  alreadyApproved: boolean;
  // Null when the user reaches every flock; else the flocks they are assigned to.
  assignedFlocks: string[] | null;
}

export type AuthorizeAnswer = { redirectUri: string } | ConsentRequest;

const authorizePath = (search: string) => `/oauth/authorize${search}`;

export function askConsent(search: string): Promise<AuthorizeAnswer> {
  return apiFetch<AuthorizeAnswer>(authorizePath(search), { method: "GET" });
}

export function approveConsent(search: string, stepUpToken: string): Promise<AuthorizeAnswer> {
  return apiFetch<AuthorizeAnswer>(authorizePath(search), {
    method: "GET", headers: { [STEP_UP_HEADER]: stepUpToken },
  });
}

export function declineConsent(search: string): Promise<AuthorizeAnswer> {
  return apiFetch<AuthorizeAnswer>(authorizePath(search), {
    method: "GET", headers: { "X-Cluckwork-Consent": "deny" },
  });
}

export type ConsentPreview = Pick<ConsentRequest, "clientName" | "verifiedDomain">;

// Before sign-in: only the app's self-chosen name and verified domain, or null when
// the request is not one the server accepts.
export async function previewConsent(search: string): Promise<ConsentPreview | null> {
  try {
    const res = await fetch(`/api/v1${authorizePath(search)}`, { headers: { "X-Cluckwork-Consent": "preview" } });
    if (!res.ok) return null;
    return (await res.json()) as ConsentPreview;
  } catch {
    return null;
  }
}
