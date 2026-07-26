import { getLastTraceId } from "./client";

// #217 — the ErrorBoundary's reporting path: POST the crash to the API, which
// writes it to the server log. Best-effort BY CONTRACT: the returned promise
// always resolves, whatever the network or server does — a reporting failure
// must never surface where it could break the fallback UI.
//
// Deliberately NOT apiPost: no bearer (the endpoint is anonymous — a crashing
// app may hold no usable token), no refresh-and-retry (a crash report must
// never trigger auth churn), no Idempotency-Key (nothing is stored).

// Client-side bounds mirroring the server's per-field truncation — the report
// stays well under the endpoint's 16 KB byte cap even with both stacks full.
const MAX_MESSAGE = 2000;
const MAX_STACK = 8000;
const MAX_ROUTE = 500;

export type ClientErrorReport = {
  message: string;
  stack?: string;
  componentStack?: string;
  scope: "app" | "screen";
  route?: string;
};

export function reportClientError(report: ClientErrorReport): Promise<void> {
  const payload = {
    message: report.message.slice(0, MAX_MESSAGE) || "(no message)",
    stack: report.stack?.slice(0, MAX_STACK),
    componentStack: report.componentStack?.slice(0, MAX_STACK),
    scope: report.scope,
    route: report.route?.slice(0, MAX_ROUTE),
    // Set at build time (VITE_APP_VERSION); absent in dev builds.
    appVersion: import.meta.env.VITE_APP_VERSION as string | undefined,
    // Joins the crash to the failed screen's last API request server-side.
    traceId: getLastTraceId() ?? undefined,
  };
  return fetch("/api/v1/client-errors", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    // The common crash follow-up is the fallback's Reload button; keepalive
    // lets the report finish through the navigation.
    keepalive: true,
  }).then(
    () => undefined,
    () => undefined,
  );
}
