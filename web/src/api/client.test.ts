import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  apiGet,
  apiPost,
  apiPut,
  apiPutBytes,
  apiDelete,
  apiFetch,
  apiGetBlob,
  login,
  logout,
  restoreSession,
  changePassword,
  ApiError,
  setOnUnauthenticated,
  setOnTokensChanged,
} from "./client";
import { getAccessToken, setAccessToken, clearAccessToken } from "../auth/tokenStore";

// The fetch client owns the SPA's session lifecycle: bearer-attach, one
// transparent refresh-and-retry on 401, single-flight refresh, and fail-closed
// teardown. Since #145 the refresh token rides an HttpOnly cookie (the browser
// attaches it), so refresh/logout send NO refresh body — only the CSRF header.
// All exercised here against a stubbed global fetch — no network.

const FUTURE = "2099-01-01T00:00:00Z";
const CSRF = "X-Cluckwork-Auth";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// The access-token body login/refresh now return (no refresh token).
function accessResponse(accessToken: string, status = 200): Response {
  return jsonResponse({ accessToken, accessTokenExpiry: FUTURE }, status);
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
  setAccessToken("at1");
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnUnauthenticated(null);
  setOnTokensChanged(null);
  clearAccessToken();
});

describe("apiFetch — happy path", () => {
  it("attaches the bearer token and returns parsed JSON", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ value: 42 }));
    const body = await apiGet<{ value: number }>("/stock");
    expect(body).toEqual({ value: 42 });
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/stock");
    expect(new Headers(init.headers).get("Authorization")).toBe("Bearer at1");
    expect(onTokens).not.toHaveBeenCalled(); // a plain GET must not touch the token
  });

  it("returns undefined for 204 No Content", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(apiFetch("/thing", { method: "DELETE" })).resolves.toBeUndefined();
  });
});

describe("apiFetch — no in-memory token", () => {
  it("attempts one silent refresh; on failure throws 401 and fires onUnauthenticated", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "no cookie" }, 401)); // the bootstrap refresh
    await expect(apiGet("/stock")).rejects.toMatchObject({ status: 401 });
    expect(onUnauth).toHaveBeenCalledTimes(1);
    // exactly one fetch — the refresh attempt — and it targeted /auth/refresh
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(callsTo(fetchMock, "/stock")).toHaveLength(0);
  });

  it("uses the refreshed token when the silent refresh succeeds", async () => {
    clearAccessToken();
    fetchMock
      .mockResolvedValueOnce(accessResponse("at2")) // silent refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // the request, now authed
    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(authOf(callsTo(fetchMock, "/stock")[0])).toBe("Bearer at2");
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
      .mockResolvedValueOnce(accessResponse("at2")) // refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // retry

    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const calls = fetchMock.mock.calls as Call[];
    // Refresh request contract (#145): POST /auth/refresh with NO body and NO
    // bearer — it authenticates by the HttpOnly cookie — plus the CSRF header.
    expect(calls[1][0]).toBe("/api/v1/auth/refresh");
    expect(calls[1][1].method).toBe("POST");
    expect(calls[1][1].body).toBeUndefined();
    expect(authOf(calls[1])).toBeNull();
    expect(headerOf(calls[1], CSRF)).toBe("1");
    // Retry hits the same URL with the refreshed token.
    expect(calls[2][0]).toBe("/api/v1/stock");
    expect(authOf(calls[2])).toBe("Bearer at2");
    expect(onTokens).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBe("at2");
  });

  it("replays a write across a refresh with the SAME idempotency key and body", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original POST
      .mockResolvedValueOnce(accessResponse("at2")) // refresh
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
        return accessResponse("at2");
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
      .mockResolvedValueOnce(accessResponse("at2"))
      .mockResolvedValueOnce(jsonResponse({ ok: 1 })) // cycle 1 retry
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // cycle 2 original
      .mockResolvedValueOnce(accessResponse("at3"))
      .mockResolvedValueOnce(jsonResponse({ ok: 2 })); // cycle 2 retry

    await apiGet("/a");
    await apiGet("/b");
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2); // a stuck latch would only refresh once
    expect(getAccessToken()).toBe("at3");
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
    expect(getAccessToken()).toBeNull();
  });

  it("does NOT retry-refresh when the retry itself 401s (one transparent refresh only)", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse("at2")) // refresh ok
      .mockResolvedValueOnce(jsonResponse({ title: "still 401" }, 401)); // retry 401

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).title).toBe("expired"); // original surfaced, no recursion
    expect(fetchMock).toHaveBeenCalledTimes(3); // original + one refresh + one retry, then stop
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBeNull();
  });

  it("surfaces the original 401 when the refresh network call rejects", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockRejectedValueOnce(new TypeError("network down")); // refresh fetch rejects

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBeNull();
  });

  it("does NOT clear the session when refresh is rate-limited (429) — keeps the token and rethrows the 429", async () => {
    // A 429 during transparent refresh is transient throttling (#143), not a
    // dead session. Wiping the token here would force a re-login through the same
    // rate limit; instead the token is kept and the 429 surfaces.
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original 401
      .mockResolvedValueOnce(jsonResponse({ title: "Too many requests" }, 429)); // refresh 429

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(429); // the 429, not the original 401
    expect(onUnauth).not.toHaveBeenCalled(); // session preserved
    expect(getAccessToken()).toBe("at1"); // token untouched
  });

  it("recovers on the next request after a failed refresh — the latch is cleared on failure too", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // req 1 original
      .mockResolvedValueOnce(jsonResponse({ title: "bad refresh" }, 401)); // req 1 refresh fails
    await apiGet("/a").catch(() => {});

    // Session was torn down; a fresh login reseeds the token and the next 401 must
    // be able to start a brand-new refresh (not reuse a rejected latch).
    setAccessToken("at1b");
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // req 2 original
      .mockResolvedValueOnce(accessResponse("at2")) // refresh ok
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

  it("apiPut declares JSON — the default no caller has to state", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await apiPut("/egg-grades/1", { name: "A" }, "put-key");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Content-Type")).toBe("application/json");
  });

  it("apiPutBytes sends the body as-is under its own content type (#123 logo upload)", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ contentHash: "abc" }));
    const bytes = new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" });

    await apiPutBytes("/account/logo", bytes, "image/png", "logo-key");

    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/account/logo");
    expect(init.method).toBe("PUT");
    // The caller's type survives: raw() must not overwrite it with JSON, or the
    // body would go up declared as something it is not.
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Content-Type")).toBe("image/png");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBe("logo-key");
    // Not JSON-encoded — the Blob itself is the body.
    expect(init.body).toBe(bytes);
  });

  it("apiPutBytes generates a key when none is given", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ contentHash: "abc" }));
    await apiPutBytes("/account/logo", new Blob(["x"]), "application/octet-stream");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "Idempotency-Key")).toBeTruthy();
  });

  it("apiPutBytes retries the SAME bytes after a 401 refresh", async () => {
    setAccessToken("stale");
    const bytes = new Blob(["png"], { type: "image/png" });
    fetchMock
      .mockResolvedValueOnce(new Response(null, { status: 401 }))   // the write
      .mockResolvedValueOnce(accessResponse("fresh"))               // the refresh
      .mockResolvedValueOnce(jsonResponse({ contentHash: "abc" })); // the retry

    await apiPutBytes("/account/logo", bytes, "image/png", "logo-key");

    const writes = callsTo(fetchMock, "/account/logo");
    expect(writes).toHaveLength(2);
    // A body consumed by the first attempt would make the retry upload nothing;
    // a Blob can be read again, which is why the retry is safe at all.
    expect(writes[1][1].body).toBe(bytes);
    expect(authOf(writes[1])).toBe("Bearer fresh");
    expect(headerOf(writes[1], "Idempotency-Key")).toBe("logo-key");
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
      .mockResolvedValueOnce(accessResponse("at2")) // refresh
      .mockResolvedValueOnce(new Response("data", { status: 200 })); // retry

    const { blob } = await apiGetBlob("/export/all");
    expect(blob.size).toBe(4); // "data"
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(authOf(fetchMock.mock.calls[2] as Call)).toBe("Bearer at2");
  });

  it("with no session, attempts one refresh, then throws 401 + fires onUnauthenticated", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "no cookie" }, 401)); // silent refresh
    await expect(apiGetBlob("/export/all")).rejects.toMatchObject({ status: 401 });
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(callsTo(fetchMock, "/export/all")).toHaveLength(0);
  });
});

describe("auth endpoints", () => {
  it("login stores the access token (memory only) and notifies listeners", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(accessResponse("atL"));
    await login({ email: "a@b.co", password: "pw" });
    expect(getAccessToken()).toBe("atL");
    expect(localStorage.length).toBe(0); // never persisted
    expect(onTokens).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/login");
    expect(new Headers(init.headers).get("Authorization")).toBeNull(); // login is unauthenticated
  });

  it("logout revokes cookie-authenticated (CSRF header, NO bearer, no body), then clears the token", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await logout();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/logout");
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined(); // refresh token is in the cookie, not the body
    expect(headerOf(fetchMock.mock.calls[0] as Call, CSRF)).toBe("1");
    // No bearer: logout authenticates by the cookie so it works even with an
    // expired access token and needs no Idempotency-Key (#145 review).
    expect(authOf(fetchMock.mock.calls[0] as Call)).toBeNull();
    expect(getAccessToken()).toBeNull();
  });

  it("logout clears the local token even when the server revoke fails", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "revoke failed" }, 500));
    await expect(logout()).resolves.toBeUndefined(); // best-effort, does not throw
    expect(getAccessToken()).toBeNull();
  });

  it("logout always calls the endpoint — a cookie may exist even with no in-memory token", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(logout()).resolves.toBeUndefined();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect((fetchMock.mock.calls[0] as Call)[0]).toBe("/api/v1/auth/logout");
  });
});

describe("changePassword (#165)", () => {
  it("posts to /auth/change-password and swaps in the returned access token", async () => {
    fetchMock.mockResolvedValueOnce(accessResponse("at-after-change"));
    // Runtime-generated so no literal secret lands in source. Built through a
    // helper rather than inline: a password-shaped literal sitting directly on a
    // `currentPassword = ...` line trips secret scanners even when the value is
    // a fresh UUID (GitGuardian flagged exactly this on PR #183).
    const freshPassword = () => `Aa1!${crypto.randomUUID()}`;
    const currentPassword = freshPassword();
    const newPassword = freshPassword();

    await changePassword({ currentPassword, newPassword });

    const call = fetchMock.mock.calls[0] as Call;
    expect(call[0]).toBe("/api/v1/auth/change-password");
    // A key rides along (apiPost adds one to every write) but is inert: the
    // SERVER exempts this route from the response cache, because replaying it
    // would return the token without the rotated Set-Cookie (#165 review). The
    // exemption itself is asserted server-side in UserPasswordTests.
    expect(authOf(call)).toBe("Bearer at1"); // authenticated write
    expect(JSON.parse(String(call[1].body))).toEqual({ currentPassword, newPassword });

    // The server rotated the session; the returned token replaces the old one so
    // this tab keeps working after every other session was revoked.
    expect(getAccessToken()).toBe("at-after-change");
    expect(onTokens).toHaveBeenCalled();
  });

  it("propagates a rejection (e.g. wrong current password) and leaves the token alone", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "Current password is incorrect." }, 400));

    await expect(changePassword({ currentPassword: "x", newPassword: "y" })).rejects.toThrow(ApiError);
    expect(getAccessToken()).toBe("at1"); // unchanged
  });
});

describe("cross-tab refresh coordination (#169)", () => {
  // A minimal Web Locks stand-in that is NAME-SCOPED, like the real API: each
  // lock name has its own FIFO queue, and different names never contend. Modelling
  // the scoping is what makes "the SUT waits behind the SAME name" a meaningful
  // assertion — if the fake ignored the name, a per-tab (non-shared) lock name
  // would look identical and the #169 fix could silently regress.
  function fakeLockManager() {
    const tails = new Map<string, Promise<unknown>>();
    const request = vi.fn((name: string, cb: () => Promise<unknown>) => {
      const prev = tails.get(name) ?? Promise.resolve();
      const run = prev.then(() => cb());
      tails.set(name, run.catch(() => {})); // next waiter proceeds even if this one throws
      return run;
    });
    return { request };
  }

  // The SUT's OWN lock acquisition (asserting on `toHaveBeenCalledWith` alone
  // would be satisfied by a test's own setup call, proving nothing about the code).
  function sutLockName(locks: ReturnType<typeof fakeLockManager>): string | undefined {
    return locks.request.mock.calls.at(-1)?.[0] as string | undefined;
  }

  it("waits for another tab's in-progress refresh before hitting the server (no cross-tab replay)", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    // Occupy the lock as 'another tab' still rotating the cookie; ours must queue.
    const otherTab = deferred<void>();
    locks.request("cluckwork.auth.refresh", () => otherTab.promise);

    clearAccessToken(); // next call forces a silent refresh
    fetchMock.mockImplementation(async (url: string) =>
      url.endsWith("/auth/refresh") ? accessResponse("at2") : jsonResponse({ ok: true }),
    );

    const inflight = apiGet<{ ok: boolean }>("/a");
    await drain();

    // Blocked behind the other tab: our refresh has NOT reached the server yet.
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(0);

    otherTab.resolve(); // the other tab finished; the cookie is now the fresh token
    const a = await inflight;

    expect(a).toEqual({ ok: true });
    // Serialized strictly after the other tab — exactly one refresh, no replay.
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    // The SUT acquired the SHARED, stable lock name — the crux of cross-tab
    // coordination. (Asserted on the code's own call, not the setup call above.)
    expect(sutLockName(locks)).toBe("cluckwork.auth.refresh");
    expect(getAccessToken()).toBe("at2");
  });

  it("a lock held under a DIFFERENT name does not block refresh (fake is name-scoped, like the real API)", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    // Some unrelated feature holds a different lock — must NOT serialise refresh.
    const unrelated = deferred<void>();
    locks.request("some-other-feature", () => unrelated.promise);

    clearAccessToken();
    fetchMock.mockImplementation(async (url: string) =>
      url.endsWith("/auth/refresh") ? accessResponse("at2") : jsonResponse({ ok: true }),
    );

    const a = await apiGet<{ ok: boolean }>("/a"); // proceeds despite the other lock
    unrelated.resolve();

    expect(a).toEqual({ ok: true });
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
  });

  it("clears the single-flight latch after a lock-guarded refresh FAILS, so a later refresh retries", async () => {
    // Covers the lock-wrapped rejection path specifically (the existing latch test
    // runs in jsdom with no navigator.locks, i.e. only the fallback branch).
    vi.stubGlobal("navigator", { locks: fakeLockManager() });
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "boom" }, 500)) // refresh 1 (under lock) fails
      .mockResolvedValueOnce(accessResponse("at2")); // refresh 2 succeeds

    await expect(restoreSession()).resolves.toBe(false);
    await expect(restoreSession()).resolves.toBe(true);

    // A stuck latch would have refused the second refresh entirely.
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2);
    expect(getAccessToken()).toBe("at2");
  });

  it("aborts and releases the lock if a refresh hangs past the timeout (no cross-tab starvation)", async () => {
    vi.useFakeTimers();
    try {
      vi.stubGlobal("navigator", { locks: fakeLockManager() });
      // A hung server: the fetch never resolves on its own — only the abort signal
      // (fired by our lock timeout) can settle it.
      fetchMock.mockImplementation(
        (_url: string, init: RequestInit) =>
          new Promise((_resolve, reject) =>
            init.signal?.addEventListener("abort", () =>
              reject(new DOMException("timed out", "AbortError")),
            ),
          ),
      );

      const restored = restoreSession(); // parks on the hung refresh
      await vi.advanceTimersByTimeAsync(20_000); // past REFRESH_TIMEOUT_MS

      // The timeout aborted the fetch and released the lock: the promise settles
      // (does not hang forever) — restoreSession catches the abort and reports no
      // session, but crucially the lock is freed for other tabs.
      await expect(restored).resolves.toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("degrades gracefully when the Web Locks API is unavailable (older browsers)", async () => {
    vi.stubGlobal("navigator", {}); // no .locks — like jsdom / older Safari
    clearAccessToken();
    fetchMock.mockImplementation(async (url: string) =>
      url.endsWith("/auth/refresh") ? accessResponse("at2") : jsonResponse({ ok: true }),
    );

    const a = await apiGet<{ ok: boolean }>("/a");

    expect(a).toEqual({ ok: true });
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1); // still refreshes, just uncoordinated
    expect(getAccessToken()).toBe("at2");
  });
});
