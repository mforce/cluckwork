import { apiFetch, apiGet, apiPost, STEP_UP_HEADER } from "../client";
import { adoptingWrite } from "../session";
import type { AccessTokenResponse } from "../types";

// #1284 — the signed-in user's own passkeys (record 1282 "What a caller
// sees"). Registration and removal can rotate the session, so they write
// through session.ts's adoptingWrite. Every /auth/passkeys route is on the
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
// The server's own limit (AuthEndpoints.PasskeyNameMaxLength).
export const PASSKEY_NAME_MAX = 64;

export const listPasskeys = (signal?: AbortSignal) => apiGet<PasskeyList>("/auth/passkeys", signal);

export const startPasskeyRegistration = () => apiPost<PasskeyCeremony>("/auth/passkeys/options");

export const renamePasskey = (id: string, name: string) =>
  apiFetch<void>(`/auth/passkeys/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify({ name }) });

// #1284 — registering a passkey hands this device a fresh pair only when it
// was the user's first factor, which ended every other session (record 1282
// decision 16); a later passkey answers 201 with the passkey alone.
export async function registerPasskey(
  body: { ceremony: string; credential: RegistrationResponseJSON; name: string }, stepUpToken: string,
): Promise<Passkey> {
  const res = await adoptingWrite<{ passkey: Passkey } & Partial<AccessTokenResponse>>("/auth/passkeys", {
    method: "POST",
    headers: { [STEP_UP_HEADER]: stepUpToken },
    body: JSON.stringify(body),
  }, { pairOptional: true });
  if (!res?.passkey) throw new Error("/auth/passkeys answered without the passkey.");
  return res.passkey;
}

// #1284 — removing one of your own passkeys ends every other session and
// hands this device a fresh pair (record 1282 decision 10).
export async function removePasskey(id: string, stepUpToken: string): Promise<void> {
  await adoptingWrite<AccessTokenResponse>(`/auth/passkeys/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { [STEP_UP_HEADER]: stepUpToken },
  });
}
