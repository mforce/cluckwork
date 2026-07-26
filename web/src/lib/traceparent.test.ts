import { describe, it, expect, vi, afterEach } from "vitest";
import { newTraceparent } from "./traceparent";

// #217 — every API request carries a W3C traceparent minted here, so a browser
// action correlates with the API's request log and spans (#214). Format:
// 00-{32 hex trace-id}-{16 hex parent-id}-{flags}; an all-zero trace-id or
// parent-id is INVALID per the spec and a receiver may discard the header.

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("newTraceparent", () => {
  it("mints a spec-shaped header with the trace id exposed separately", () => {
    const tp = newTraceparent();
    expect(tp.header).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    expect(tp.header.split("-")[1]).toBe(tp.traceId);
  });

  it("mints a different trace id per call", () => {
    expect(newTraceparent().traceId).not.toBe(newTraceparent().traceId);
  });

  it("never emits the all-zero ids the spec declares invalid", () => {
    // Force the RNG to hand back zeros once per segment; the generator must
    // retry rather than emit 00000000000000000000000000000000.
    const real = crypto.getRandomValues.bind(crypto);
    let zeroCalls = 2; // first trace-id draw + first parent-id draw
    vi.stubGlobal("crypto", {
      getRandomValues: (buf: Uint8Array<ArrayBuffer>) => {
        if (zeroCalls > 0) {
          zeroCalls--;
          buf.fill(0);
          return buf;
        }
        return real(buf);
      },
    });

    const tp = newTraceparent();
    expect(tp.traceId).not.toBe("0".repeat(32));
    expect(tp.header.split("-")[2]).not.toBe("0".repeat(16));
    expect(tp.header).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
  });
});
