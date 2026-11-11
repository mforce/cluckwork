import { apiGet, apiPost, STEP_UP_HEADER } from "../client";

// #1277 — the signed-in user's own two-factor authentication. Confirm and
// turn-off rotate the session, so they live with the token writers in
// api/session.ts.

export interface TwoFactorStatus {
  enabled: boolean;
  recoveryCodesLeft: number;
  // #1281 — an Owner's grace deadline (ISO 8601), sent only while
  // TwoFactor:RequireForOwners is on, and still sent once two-factor is on.
  requiredBy?: string;
}

// The key and the URI are credentials: the enrolment screen shows them and
// keeps them nowhere else.
export interface EnrolmentStart {
  key: string;
  otpauthUri: string;
}

export const getTwoFactorStatus = (signal?: AbortSignal) => apiGet<TwoFactorStatus>("/auth/two-factor", signal);

// apiPost sends a fresh Idempotency-Key: the server keeps no response for
// these routes, so a reused key would replay nothing.
export const startTwoFactor = (stepUpToken: string) =>
  apiPost<EnrolmentStart>("/auth/two-factor/start", undefined, undefined, { [STEP_UP_HEADER]: stepUpToken });

// Optional on the type as on the wire: the caller must check a 200 for it.
export const regenerateRecoveryCodes = (stepUpToken: string) =>
  apiPost<{ recoveryCodes?: string[] }>("/auth/two-factor/recovery-codes", undefined, undefined, { [STEP_UP_HEADER]: stepUpToken });
