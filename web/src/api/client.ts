// #308/#360/#606 — carries a short-lived step-up grant (see stepUp() in
// ./session) on the user-administration calls that require recent proof (every user
// creation, every administrative password reset, every role change,
// login-email change, disable/re-enable, and every flock assign/unassign).
// Mirrors AuthEndpoints.StepUpHeaderName.
export const STEP_UP_HEADER = "X-Cluckwork-Step-Up";

export { apiGet, apiPost, apiPut, apiPutBytes, apiDelete, apiGetBlob, apiFetch, restoreSession } from "./refresh";
export { login, changePassword, stepUp, logout } from "./session";
export { setOnUnauthenticated, setOnTokensChanged } from "./sessionState";
export { getLastTraceId } from "./trace";
export { ApiError } from "./errors";
