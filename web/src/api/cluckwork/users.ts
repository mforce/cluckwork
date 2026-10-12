import { apiDelete, apiGet, apiPost, apiPut, STEP_UP_HEADER } from "../client";
import type { Created } from "./shared";

// --- Users (#73, admin-only endpoints) ---

export interface User {
  id: string;
  email: string;
  displayName: string | null;
  role: string; // "Admin" | "Worker"
  // #356 — ISO timestamp of when this user was disabled, or null while active.
  // The SPA renders the row muted with a "Disabled" badge when this is set.
  disabledAt: string | null;
}

export const listUsers = () => apiGet<User[]>("/users");

// #799 — one person's connection to one app. appName is the app's own choice (#797).
export interface AppConnection {
  userId: string;
  clientId: string;
  appName: string | null;
  scopes: string[];
  connectedAtUtc: string;
  lastUsedAtUtc: string | null;
}

export const listMyConnectedApps = () => apiGet<AppConnection[]>("/me/connected-apps");
export const disconnectMyApp = (clientId: string) =>
  apiDelete<void>(`/me/connected-apps?clientId=${encodeURIComponent(clientId)}`);
export const listFarmConnectedApps = () => apiGet<AppConnection[]>("/users/connected-apps");
export const disconnectUserApp = (userId: string, clientId: string) =>
  apiDelete<void>(`/users/${userId}/connected-apps?clientId=${encodeURIComponent(clientId)}`);

export interface FlockAssignment {
  id: string;
  flockId: string | null;
  // #512 US4 (T047/T050) — the assigned flock's CURRENT name, resolved via a
  // scoped left join. Null for a farm-wide assignment (`flockId === null`)
  // or when the flock fell out of scope; never an ID fragment.
  flockName: string | null;
}

export const listFlockAssignments = (userId: string) =>
  apiGet<FlockAssignment[]>(`/users/${userId}/flock-assignments`);

// #606 — stepUpToken is required by the server UNCONDITIONALLY: every
// interactive assignment or removal changes durable account access,
// regardless of the target's role.
export const assignFlock = (userId: string, flockId: string, key?: string, stepUpToken?: string) =>
  apiPost<Created>(
    `/users/${userId}/flock-assignments`, { flockId }, key,
    stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

export const unassignFlock = (userId: string, assignmentId: string, key?: string, stepUpToken?: string) =>
  apiDelete<void>(
    `/users/${userId}/flock-assignments/${assignmentId}`, key,
    stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// interactive user creation establishes a durable login, regardless of the
// created role. Callers get it from api/client.ts's stepUp().
export const createUser = (body: {
  email: string; password: string; role: string; name?: string;
}, key?: string, stepUpToken?: string) => apiPost<Created>(
  "/users", body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #163 — edit a user's display name. `name: null` clears it back to "—".
export const updateUser = (id: string, body: { name: string | null }, key?: string) =>
  apiPut<void>(`/users/${id}`, body, key);

// #165 — an Owner sets a user's password without knowing the current one. The
// server signs that user out of every device.
//
// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// administrative reset replaces an authenticator, regardless of the target's
// current role.
export const setUserPassword = (
  id: string, body: { newPassword: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/password`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #355 — promote/demote an existing user's role. The server signs that user
// out of every device.
//
// #308/#360 — stepUpToken is required by the server UNCONDITIONALLY: every
// role change mutates a durable authorization set, including no-ops and
// apparent demotions. Server also refuses self-targeting (400
// Users.CannotChangeOwnRole) — surfaced as an ordinary ApiError, not
// special-cased client-side.
export const changeUserRole = (
  id: string, body: { role: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/role`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

export const changeUserEmail = (
  id: string, body: { email: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/email`, body, key,
  stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined,
);

// #356/#360 — disable a user: revokes every session and refuses further
// sign-in. Its step-up proof is unconditional like create/reset/role: every
// disable requires stepUpToken regardless of the target's role. Server also
// refuses self-targeting (400 Users.CannotDisableSelf), surfaced as an ordinary
// ApiError like every other domain refusal.
export const disableUser = (
  id: string, body: { reason: string | null }, key?: string, stepUpToken?: string,
) => apiPost<void>(
  `/users/${id}/disable`, body, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);

// #356/#360 — re-enable a disabled user. No body: there is no free-text field,
// only the route id and the step-up header, required unconditionally like
// create/reset/role and disable.
export const enableUser = (
  id: string, key?: string, stepUpToken?: string,
) => apiPost<void>(
  `/users/${id}/enable`, undefined, key, stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined);
