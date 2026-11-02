import type { ApiError } from "../api/errors";
import i18n from "../i18n";

// #1274 "What a caller sees": the second-factor answers shared by the login
// code step, step-up and change-password. Null for any other error.
export function secondFactorMessage(err: ApiError): string | null {
  switch (err.title) {
    case "Auth.SecondFactorRequired": return i18n.t("auth:secondFactorRequired");
    case "Auth.SecondFactorIncorrect": return i18n.t("auth:secondFactorIncorrect");
    case "Auth.SecondFactorKeyUnavailable": return i18n.t("auth:secondFactorKeyUnavailable");
    case "Auth.SecondFactorThrottled": return throttled(err.retryAfterSeconds);
    case "Auth.StepUpRetry": return i18n.t("auth:stepUpRetry");
    default: return null;
  }
}

// The daily budget reopens up to 24 hours later, so the wait is rounded up to
// whole minutes, and to whole hours from two hours on.
function throttled(seconds: number | null): string {
  if (seconds === null) return i18n.t("auth:secondFactorThrottled");
  const minutes = Math.ceil(seconds / 60);
  return minutes < 120
    ? i18n.t("auth:secondFactorThrottledMinutes", { count: minutes })
    : i18n.t("auth:secondFactorThrottledHours", { count: Math.ceil(minutes / 60) });
}
