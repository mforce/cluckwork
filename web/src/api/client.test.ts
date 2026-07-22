import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  apiGet,
  apiPost,
  apiPut,
  apiDelete,
  apiFetch,
  apiGetBlob,
  login,
  logout,
  ApiError,
  setOnUnauthenticated,
  setOnTokensChanged,
} from "./client";
import { saveTokens, loadTokens, clearTokens } from "../auth/tokenStore";

// The fetch client owns the SPA's session lifecycle: bearer-attach, one
// transparent refresh-and-retry on 401, single-flight refresh, and fail-closed
// teardown. All exercised here against a stubbed global fetch — no network.

const FUTURE = "2099-01-01T00:00:00Z";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

type Call = [string, RequestInit];

function authOf(call: Call): string | null {
  return new Headers(call[1]?.headers ?? {}).get("Authorization");
}

function headerOf(call: Call, name: string): string | null {
  return new Headers(call[1]?.headers ?? {}).get(name);
}

function callsTo(mock: ReturnType<typeof vi.fn>, suffix: string): Call[] {
  return (mock.mock.calls as Call[]).filter(([url]) => url.endsWith(suffix));
}

// A promise whose resolution the test controls — lets us hold a refresh open
// until both concurrent callers have provably parked on it (no timing guesses).
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

// Runs pending microtasks AND the macrotask queue once, so every in-flight
// fetch resolution + catch handler has settled before we assert / release.
const drain = () => new Promise((r) => setTimeout(r, 0));

let fetchMock: ReturnType<typeof vi.fn>;
let onUnauth: ReturnType<typeof vi.fn>;
let onTokens: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  onUnauth = vi.fn();
  onTokens = vi.fn();
  setOnUnauthenticated(onUnauth);
  setOnTokensChanged(onTokens);
  saveTokens({ accessToken: "at1", refreshToken: "rt1", expiresAt: FUTURE });
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnUnauthenticated(null);
  setOnTokensChanged(null);
  clearTokens();
});

describe("apiFetch — happy path", () => {
  it("attaches the bearer token and returns parsed JSON", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ value: 42 }));
    const body = await apiGet<{ value: number }>("/stock");
    expect(body).toEqual({ value: 42 });
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/stock");
    expect(new Headers(init.headers).get("Authorization")).toBe("Bearer at1");
    expect(onTokens).not.toHaveBeenCalled(); // a plain GET must not touch the token pair
  });

  it("returns undefined for 204 No Content", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(apiFetch("/thing", { method: "DELETE" })).resolves.toBeUndefined();
  });
});

describe("apiFetch — no session", () => {
  it("throws 401 and fires onUnauthenticated without calling fetch", async () => {
    clearTokens();
    await expect(apiGet("/stock")).rejects.toMatchObject({ status: 401 });
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("apiFetch — error mapping", () => {
  it("passes a non-401 error through without attempting a refresh", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "Bad", detail: "nope" }, 400));
    await expect(apiGet("/stock")).rejects.toMatchObject({ status: 400, title: "Bad" });
    expect(fetchMock).toHaveBeenCalledTimes(1); // no refresh on non-401
  });

  it("flattens a ValidationProblem errors map into the message", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ title: "Validation failed", errors: { name: ["required"], qty: ["too big", "nan"] } }, 400),
    );
    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).title).toBe("Validation failed");
    expect((err as ApiError).message).toBe("required too big nan");
  });

  it("keeps status text when the error body is not JSON", async () => {
    fetchMock.mockResolvedValueOnce(new Response("<html>502</html>", { status: 502, statusText: "Bad Gateway" }));
    await expect(apiGet("/stock")).rejects.toMatchObject({ status: 502, title: "Bad Gateway" });
  });
});

describe("apiFetch — transparent refresh", () => {
  it("on 401, refreshes once and retries the original request with the new token", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE })) // refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // retry

    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const calls = fetchMock.mock.calls as Call[];
    // Refresh request contract: POST /auth/refresh with the stored refresh
    // token and NO bearer (it authenticates by the refresh token alone).
    expect(calls[1][0]).toBe("/api/v1/auth/refresh");
    expect(calls[1][1].method).toBe("POST");
    expect(calls[1][1].body).toBe(JSON.stringify({ refreshToken: "rt1" }));
    expect(authOf(calls[1])).toBeNull();
    // Retry hits the same URL with the refreshed token.
    expect(calls[2][0]).toBe("/api/v1/stock");
    expect(authOf(calls[2])).toBe("Bearer at2");
    expect(onTokens).toHaveBeenCalledTimes(1);
    expect(loadTokens()?.accessToken).toBe("at2");
  });

  it("replays a write across a refresh with the SAME idempotency key and body", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original POST
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE })) // refresh
      .mockResolvedValueOnce(jsonResponse({ id: "1" })); // retry POST

    await apiPost("/customers", { name: "x" }, "fixed-key");
    const calls = fetchMock.mock.calls as Call[];
    const original = calls[0];
    const retry = calls[2];
    expect(original[1].method).toBe("POST");
    expect(retry[1].method).toBe("POST");
    expect(retry[1].body).toBe(original[1].body); // same payload, not re-serialized differently
    // Same idempotency key → the server dedupes the replay instead of double-writing.
    expect(headerOf(retry, "Idempotency-Key")).toBe("fixed-key");
    expect(headerOf(original, "Idempotency-Key")).toBe("fixed-key");
    expect(authOf(retry)).toBe("Bearer at2"); // only the bearer changed
  });

  it("shares a single in-flight refresh across concurrent 401s (single-flight)", async () => {
    const gate = deferred<void>();
    let refreshes = 0;
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url.endsWith("/auth/refresh")) {
        refreshes += 1;
        if (refreshes > 1) throw new Error("a second refresh must not start while one is in flight");
        await gate.promise; // hold open until both 401s have parked on this refresh
        return jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE });
      }
      const auth = new Headers(init.headers).get("Authorization");
      return auth === "Bearer at1" ? jsonResponse({ title: "expired" }, 401) : jsonResponse({ ok: true });
    });

    const inflight = Promise.all([apiGet<{ ok: boolean }>("/a"), apiGet<{ ok: boolean }>("/b")]);
    await drain(); // both 401s land, both callers park on the one refresh
    gate.resolve();
    const [a, b] = await inflight;

    expect(a).toEqual({ ok: true });
    expect(b).toEqual({ ok: true });
    expect(refreshes).toBe(1); // exactly one refresh, and the guard proves no second one started
    expect(onTokens).toHaveBeenCalledTimes(1);
  });

  it("refreshes again on a later 401 — the single-flight latch is cleared on success", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // cycle 1 original
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE }))
      .mockResolvedValueOnce(jsonResponse({ ok: 1 })) // cycle 1 retry
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // cycle 2 original
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at3", refreshToken: "rt3", expiresAt: FUTURE }))
      .mockResolvedValueOnce(jsonResponse({ ok: 2 })); // cycle 2 retry

    await apiGet("/a");
    await apiGet("/b");
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2); // a stuck latch would only refresh once
    expect(loadTokens()?.accessToken).toBe("at3");
  });
});

describe("apiFetch — refresh failure is fail-closed and non-recursive", () => {
  it("clears the session and rethrows the ORIGINAL error when refresh 401s", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original 401
      .mockResolvedValueOnce(jsonResponse({ title: "bad refresh token" }, 401)); // refresh 401

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).title).toBe("expired"); // original, not the refresh error
    expect(fetchMock).toHaveBeenCalledTimes(2); // no retry after refresh failed
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(loadTokens()).toBeNull();
  });

  it("does NOT retry-refresh when the retry itself 401s (one transparent refresh only)", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE })) // refresh ok
      .mockResolvedValueOnce(jsonResponse({ title: "still 401" }, 401)); // retry 401

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).title).toBe("expired"); // original surfaced, no recursion
    expect(fetchMock).toHaveBeenCalledTimes(3); // original + one refresh + one retry, then stop
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(loadTokens()).toBeNull();
  });

  it("surfaces the original 401 when the refresh network call rejects", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockRejectedValueOnce(new TypeError("network down")); // refresh fetch rejects

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(loadTokens()).toBeNull();
  });

  it("does NOT clear the session when refresh is rate-limited (429) — keeps tokens and rethrows the 429", async () => {
    // A 429 during transparent refresh is transient throttling (#143), not a
    // dead session. Wiping tokens here would force a re-login through the same
    // rate limit; instead the refresh token is kept and the 429 surfaces.
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original 401
      .mockResolvedValueOnce(jsonResponse({ title: "Too many requests" }, 429)); // refresh 429

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(429); // the 429, not the original 401
    expect(onUnauth).not.toHaveBeenCalled(); // session preserved
    expect(loadTokens()?.refreshToken).toBe("rt1"); // tokens untouched
  });

  it("recovers on the next request after a failed refresh — the latch is cleared on failure too", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // req 1 original
      .mockResolvedValueOnce(jsonResponse({ title: "bad refresh" }, 401)); // req 1 refresh fails
    await apiGet("/a").catch(() => {});

    // Session was torn down; a fresh login reseeds tokens and the next 401 must
    // be able to start a brand-new refresh (not reuse a rejected latch).
    saveTokens({ accessToken: "at1b", refreshToken: "rt1b", expiresAt: FUTURE });
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // req 2 original
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE })) // refresh ok
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // req 2 retry
    await expect(apiGet<{ ok: boolean }>("/b")).resolves.toEqual({ ok: true });
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2);
  });
});

describe("write idempotency", () => {
  it("apiPost generates an Idempotency-Key when none is given", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/customers", { name: "x" });
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBeTruthy();
  });

  it("generates a DISTINCT key per call (so two writes are not deduped as one)", async () => {
    fetchMock.mockImplementation(() => Promise.resolve(jsonResponse({ id: "1" }))); // fresh body each call
    await apiPost("/customers", { name: "a" });
    await apiPost("/customers", { name: "b" });
    const k0 = headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key");
    const k1 = headerOf(fetchMock.mock.calls[1] as Call, "Idempotency-Key");
    expect(k0).toBeTruthy();
    expect(k1).toBeTruthy();
    expect(k0).not.toBe(k1);
  });

  it("apiPost honours an explicit Idempotency-Key (retry-safe replay)", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/customers", { name: "x" }, "my-fixed-key");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBe("my-fixed-key");
  });

  it("apiPut sends method PUT with an Idempotency-Key", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await apiPut("/egg-grades/1", { name: "A" }, "put-key");
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/egg-grades/1");
    expect(init.method).toBe("PUT");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBe("put-key");
  });

  it("apiDelete sends method DELETE with an Idempotency-Key", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await apiDelete("/sales/1/items/2", "del-key");
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/sales/1/items/2");
    expect(init.method).toBe("DELETE");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBe("del-key");
  });
});

describe("apiGetBlob — file download", () => {
  it("returns the blob and parses the Content-Disposition filename", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("col1,col2\n", {
        status: 200,
        headers: { "Content-Type": "text/csv", "Content-Disposition": 'attachment; filename="export.csv"' },
      }),
    );
    const { blob, filename } = await apiGetBlob("/export/all");
    expect(filename).toBe("export.csv");
    expect(blob.size).toBe(10); // "col1,col2\n" — body flowed through unread
    expect(authOf(fetchMock.mock.calls[0] as Call)).toBe("Bearer at1");
  });

  it("has null filename when no Content-Disposition is present", async () => {
    fetchMock.mockResolvedValueOnce(new Response("x", { status: 200 }));
    const { filename } = await apiGetBlob("/export/flocks");
    expect(filename).toBeNull();
  });

  it("transparently refreshes on 401 and retries the download with the new token", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original blob request
      .mockResolvedValueOnce(jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE })) // refresh
      .mockResolvedValueOnce(new Response("data", { status: 200 })); // retry

    const { blob } = await apiGetBlob("/export/all");
    expect(blob.size).toBe(4); // "data"
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(authOf(fetchMock.mock.calls[2] as Call)).toBe("Bearer at2");
  });

  it("with no session, throws 401 + fires onUnauthenticated and never fetches", async () => {
    clearTokens();
    await expect(apiGetBlob("/export/all")).rejects.toMatchObject({ status: 401 });
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("auth endpoints", () => {
  it("login stores the token pair and notifies listeners", async () => {
    clearTokens();
    fetchMock.mockResolvedValueOnce(jsonResponse({ accessToken: "atL", refreshToken: "rtL", expiresAt: FUTURE }));
    const tokens = await login({ email: "a@b.co", password: "pw" });
    expect(tokens.accessToken).toBe("atL");
    expect(loadTokens()?.accessToken).toBe("atL");
    expect(onTokens).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/login");
    expect(new Headers(init.headers).get("Authorization")).toBeNull(); // login is unauthenticated
  });

  it("logout revokes the refresh token server-side, then clears local tokens", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await logout();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/logout");
    expect(init.method).toBe("POST");
    expect(init.body).toBe(JSON.stringify({ refreshToken: "rt1" }));
    expect(authOf(fetchMock.mock.calls[0] as Call)).toBe("Bearer at1");
    expect(loadTokens()).toBeNull();
  });

  it("logout clears local tokens even when the server revoke fails", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "revoke failed" }, 500));
    await expect(logout()).resolves.toBeUndefined(); // best-effort, does not throw
    expect(loadTokens()).toBeNull();
  });

  it("logout with no stored session is a no-op (no request)", async () => {
    clearTokens();
    await expect(logout()).resolves.toBeUndefined();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
