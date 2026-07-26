import { describe, it, expect, vi, beforeEach } from "vitest";
import { reportClientError } from "./errorReport";
import { apiGet } from "./client";
import { setAccessToken } from "../auth/tokenStore";

// #217 — the ErrorBoundary's reporting path. Best-effort by contract: whatever
// the network or server does, the returned promise RESOLVES — a reporting
// failure must never surface where it could break the fallback UI.

type Call = [string, RequestInit];

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
  vi.stubGlobal("fetch", fetchMock);
});

function sentBody(): Record<string, unknown> {
  const [, init] = fetchMock.mock.calls.at(-1) as Call;
  return JSON.parse(init.body as string) as Record<string, unknown>;
}

describe("reportClientError", () => {
  it("POSTs the report with keepalive so it survives an imminent reload", async () => {
    await reportClientError({
      message: "kaboom",
      stack: "Error: kaboom\n  at Crash",
      componentStack: "\n  at Crash\n  at ErrorBoundary",
      scope: "screen",
      route: "/daily-entries",
    });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/client-errors");
    expect(init.method).toBe("POST");
    expect(init.keepalive).toBe(true);
    expect(new Headers(init.headers).get("Content-Type")).toBe("application/json");
    expect(sentBody()).toMatchObject({
      message: "kaboom",
      stack: "Error: kaboom\n  at Crash",
      componentStack: "\n  at Crash\n  at ErrorBoundary",
      scope: "screen",
      route: "/daily-entries",
    });
  });

  it("applies the per-field caps without shrinking when the payload already fits", async () => {
    // Message over its cap, but the total stays under the byte budget: the
    // message is cut to its cap and the (already-fitting) stack is untouched.
    await reportClientError({
      message: "m".repeat(5000),
      stack: "s".repeat(3000),
      scope: "app",
    });

    const body = sentBody();
    expect((body.message as string).length).toBe(2000);
    expect((body.stack as string).length).toBe(3000);
  });

  it("substitutes a placeholder when the error carries no message", async () => {
    await reportClientError({ message: "", scope: "screen" });
    expect(sentBody().message).toBe("(no message)");
  });

  it("substitutes the placeholder for a whitespace-only message the server would reject", async () => {
    await reportClientError({ message: "   ", scope: "screen" });
    expect(sentBody().message).toBe("(no message)");
  });

  it("keeps the worst-case serialized payload under the server's byte cap", async () => {
    // Per-field caps alone would sum past 16 KB (message 2000 + two stacks at
    // 8000 + route 500 + JSON overhead) and the server would silently 413 the
    // exact deep-component-tree crash this exists for. The budget is the
    // SERIALIZED body, not the fields.
    await reportClientError({
      message: "m".repeat(5000),
      stack: "s".repeat(10_000),
      componentStack: "c".repeat(10_000),
      scope: "screen",
      route: "/r".repeat(1000),
    });

    const [, init] = fetchMock.mock.calls.at(-1) as Call;
    expect(new TextEncoder().encode(init.body as string).length).toBeLessThanOrEqual(15_000);
    const body = sentBody();
    expect((body.stack as string).length).toBeGreaterThan(0);
    expect(body.message as string).toContain("m");
  });

  it("fits the byte budget even when every field is multi-byte text", async () => {
    // "💥" is 4 UTF-8 bytes per glyph (2 UTF-16 code units): a char-count cap
    // that fits ASCII would blow the BYTE cap here.
    await reportClientError({
      message: "💥".repeat(2500),
      stack: "💥".repeat(5000),
      componentStack: "💥".repeat(5000),
      scope: "app",
    });

    const [, init] = fetchMock.mock.calls.at(-1) as Call;
    expect(new TextEncoder().encode(init.body as string).length).toBeLessThanOrEqual(15_000);
  });

  it("carries its own traceparent like every other SPA request", async () => {
    await reportClientError({ message: "kaboom", scope: "screen" });
    const [, init] = fetchMock.mock.calls[0] as Call;
    expect(new Headers(init.headers).get("traceparent")).toMatch(
      /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/,
    );
  });

  it("includes the trace id of the last API request when one was made", async () => {
    setAccessToken("token-1");
    fetchMock.mockResolvedValueOnce(
      new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    await apiGet("/flocks");
    const apiCall = fetchMock.mock.calls.find(([url]) =>
      (url as string).endsWith("/flocks"),
    ) as Call;
    const traceparent = new Headers(apiCall[1].headers).get("traceparent")!;
    const traceId = traceparent.split("-")[1];

    await reportClientError({ message: "kaboom", scope: "screen" });

    expect(sentBody().traceId).toBe(traceId);
  });

  it("resolves when the network fails — reporting is fire-and-forget", async () => {
    fetchMock.mockRejectedValue(new TypeError("network down"));
    await expect(
      reportClientError({ message: "kaboom", scope: "screen" }),
    ).resolves.toBeUndefined();
  });

  it("resolves when the server rejects the report (e.g. rate-limited)", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 429 }));
    await expect(
      reportClientError({ message: "kaboom", scope: "screen" }),
    ).resolves.toBeUndefined();
  });
});
