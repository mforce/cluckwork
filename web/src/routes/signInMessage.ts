import { ApiError } from "../api/client";
import { FARM_SUSPENDED } from "../api/session";
import i18n from "../i18n";

// #532 — this and FARM_SUSPENDED ride the ProblemDetails `title` on a 401, so they MUST be matched
// before messageFor's generic 401 branch. Without that, a suspended farm's staff
// are told their password is wrong, which is the exact outcome the maintainer's
// decision to use a distinct code exists to avoid.
const UNKNOWN_FARM_CODE = "Auth.UnknownFarmCode";

// MODULE-LEVEL — called from the sign-in pages' catch handlers, not from render, so the
// useTranslation hook is not in scope here. The imperative i18n singleton
// (already initialised, already holding the resolved language) is the correct
// tool outside render (#182).
export function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    // #532 — ahead of the generic 401 below, which would otherwise report a
    // suspended farm or a mistyped farm code as a bad password.
    if (err.title === UNKNOWN_FARM_CODE) return i18n.t("auth:unknownFarmCode");
    if (err.title === FARM_SUSPENDED) return i18n.t("auth:farmSuspended");
    if (err.status === 401) return i18n.t("auth:invalidCredentials");
    // Rate limited (#143) — too many attempts from this address.
    if (err.status === 429) return i18n.t("auth:tooManyAttempts");
    // #309 — an oversized-credential validation error (400, e.g. a >256-char
    // email/password). ApiError already carries the server's real, non-
    // enumerating message (parseError flattens body.detail / body.errors), so
    // show that instead of the generic apiDown copy, which would misleadingly
    // suggest the API itself is unreachable.
    if (err.status === 400) return err.message || i18n.t("auth:apiDown");
    // #309 — the request body exceeded the endpoint's byte cap (413), which in
    // practice means an implausibly long email/password.
    if (err.status === 413) return i18n.t("auth:credentialsTooLong");
  }
  return i18n.t("auth:apiDown");
}
