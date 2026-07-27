import { getLastTraceId } from "./client";
import { newTraceparent } from "../lib/traceparent";

// #217 — the ErrorBoundary's reporting path: POST the crash to the API, which
// writes it to the server log. Best-effort BY CONTRACT: the returned promise
// always resolves, whatever the network or server does — a reporting failure
// must never surface where it could break the fallback UI.
//
// Deliberately NOT apiPost: no bearer (the endpoint is anonymous — a crashing
// app may hold no usable token), no refresh-and-retry (a crash report must
// never trigger auth churn), no Idempotency-Key (nothing is stored).

// The binding budget is the SERIALIZED body in BYTES — the server rejects
// past 16 KB and a 413'd report is silently lost, so per-field caps alone
// (which sum past the cap, and count chars, not bytes) cannot be the
// guarantee. Fields start at these sizes and every sized field shrinks by
// halves until the payload fits under 15 000 bytes (headroom for the 202
// path's request overhead).
const MAX_BODY_BYTES = 15_000;
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
  // Trimmed before the emptiness check: the server 400s a whitespace-only
  // message, which would silently drop the report.
  const message = report.message.trim();
  const build = (scale: number) =>
    JSON.stringify({
      message: message.slice(0, Math.floor(MAX_MESSAGE * scale)) || "(no message)",
      stack: report.stack?.slice(0, Math.floor(MAX_STACK * scale)),
      componentStack: report.componentStack?.slice(0, Math.floor(MAX_STACK * scale)),
      scope: report.scope,
      route: report.route?.slice(0, Math.floor(MAX_ROUTE * scale)),
      // Set at build time (VITE_APP_VERSION); absent in dev builds.
      appVersion: import.meta.env.VITE_APP_VERSION as string | undefined,
      // Joins the crash to the failed screen's last API request server-side.
      traceId: getLastTraceId() ?? undefined,
    });

  const byteLength = (s: string) => new TextEncoder().encode(s).length;
  let scale = 1;
  let body = build(scale);
  // Terminates: at the floor every sized field is a handful of characters and
  // the skeleton is a few hundred bytes, far under the budget.
  while (byteLength(body) > MAX_BODY_BYTES && scale > 1 / 1024) {
    scale /= 2;
    body = build(scale);
  }

  return fetch("/api/v1/client-errors", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      // The report is an SPA API request like any other, so it carries its
      // own traceparent; the PAYLOAD's traceId still points at the crashed
      // screen's last request — the causal one.
      traceparent: newTraceparent().header,
    },
    body,
    // The common crash follow-up is the fallback's Reload button; keepalive
    // lets the report finish through the navigation.
    keepalive: true,
  }).then(
    () => undefined,
    () => undefined,
  );
}
