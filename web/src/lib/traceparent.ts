// #217 — W3C Trace Context (https://www.w3.org/TR/trace-context/). The SPA
// mints a `traceparent` per API request so a browser-initiated action
// correlates with the API's request log and exported spans (#214): the id the
// browser sent IS the TraceId on the server's completion event. ASP.NET Core
// honors the header natively; the API's AlwaysOnSampler means the flags byte
// cannot suppress server-side sampling either way.

export type Traceparent = { header: string; traceId: string };

export function newTraceparent(): Traceparent {
  const traceId = randomNonZeroHex(16);
  const parentId = randomNonZeroHex(8);
  return { header: `00-${traceId}-${parentId}-01`, traceId };
}

// An all-zero trace-id or parent-id is the spec's "invalid" sentinel — a
// receiver may discard the whole header. getRandomValues handing back all
// zeros is astronomically unlikely, but the guard is one loop check.
function randomNonZeroHex(byteLength: number): string {
  const buf = new Uint8Array(byteLength);
  do {
    crypto.getRandomValues(buf);
  } while (buf.every((b) => b === 0));
  return Array.from(buf, (b) => b.toString(16).padStart(2, "0")).join("");
}
