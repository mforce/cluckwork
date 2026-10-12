import { newTraceparent } from "../lib/traceparent";

// #217 — the trace id of the most recent API request, success or failure (a
// FAILED request's id is exactly the one worth having: the crash report
// attaches it, joining a browser crash to that request's server-side story).
let lastTraceId: string | null = null;
export function getLastTraceId(): string | null {
  return lastTraceId;
}

// Mint per request, never reused: the server treats the incoming id as the
// request's TraceId (#214), so sharing one across requests would fuse
// unrelated request logs into one trace.
export function attachTraceparent(headers: Headers): void {
  const tp = newTraceparent();
  headers.set("traceparent", tp.header);
  lastTraceId = tp.traceId;
}
