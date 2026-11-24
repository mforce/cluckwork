import { apiFetch, apiGet, apiPost } from "../client";

// #1284 — the signed-in user's own passkeys (record 1282 "What a caller
// sees"). Registration and removal rotate the session, so they live with the
// token writers in api/session.ts. Every /auth/passkeys route is on the
// server's ResponseNotCacheable list, so no Idempotency-Key matters here.

// id is the credential id, base64url. address is set only when two or more
// origins are configured (decision 14).
export interface Passkey {
  id: string;
  name: string | null;
  createdAt: string;
  synced: boolean;
  address: string | null;
}

// enabled: this address runs passkey ceremonies. While it is false the list
// still holds every passkey, for removal (decision 5).
export interface PasskeyList {
  enabled: boolean;
  passkeys: Passkey[];
}

// options is Identity's PublicKeyCredentialCreationOptions JSON, for
// PublicKeyCredential.parseCreationOptionsFromJSON.
export interface PasskeyCeremony {
  ceremony: string;
  options: PublicKeyCredentialCreationOptionsJSON;
}

export const MAX_PASSKEYS = 5;

export const listPasskeys = (signal?: AbortSignal) => apiGet<PasskeyList>("/auth/passkeys", signal);

export const startPasskeyRegistration = () => apiPost<PasskeyCeremony>("/auth/passkeys/options");

export const renamePasskey = (id: string, name: string) =>
  apiFetch<void>(`/auth/passkeys/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify({ name }) });
