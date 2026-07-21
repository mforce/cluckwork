import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  apiGet,
  apiPost,
  apiFetch,
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

function authOf(call: [string, RequestInit]): string | null {
  return new Headers(call[1].headers).get("Authorization");
}

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
  clearTokens();
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
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/stock");
    expect(new Headers(init.headers).get("Authorization")).toBe("Bearer at1");
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

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls[1][0]).toBe("/api/v1/auth/refresh");
    expect(authOf(calls[2])).toBe("Bearer at2"); // retry used the refreshed token
    expect(onTokens).toHaveBeenCalledTimes(1);
    expect(loadTokens()?.accessToken).toBe("at2");
  });

  it("shares a single in-flight refresh across concurrent 401s (single-flight)", async () => {
    let refreshes = 0;
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url.endsWith("/auth/refresh")) {
        refreshes += 1;
        await new Promise((r) => setTimeout(r, 0)); // let both 401s land before this resolves
        return jsonResponse({ accessToken: "at2", refreshToken: "rt2", expiresAt: FUTURE });
      }
      const auth = new Headers(init.headers).get("Authorization");
      return auth === "Bearer at1" ? jsonResponse({ title: "expired" }, 401) : jsonResponse({ ok: true });
    });

    const [a, b] = await Promise.all([apiGet<{ ok: boolean }>("/a"), apiGet<{ ok: boolean }>("/b")]);
    expect(a).toEqual({ ok: true });
    expect(b).toEqual({ ok: true });
    expect(refreshes).toBe(1); // both callers reused the one refresh
    expect(onTokens).toHaveBeenCalledTimes(1);
  });

  it("clears the session and rethrows the ORIGINAL error when refresh fails", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original 401
      .mockResolvedValueOnce(jsonResponse({ title: "bad refresh token" }, 401)); // refresh 401

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).title).toBe("expired"); // original, not the refresh error
    expect(fetchMock).toHaveBeenCalledTimes(2); // no retry after refresh failed
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(loadTokens()).toBeNull(); // tokens cleared
  });
});

describe("write idempotency", () => {
  it("apiPost generates an Idempotency-Key when none is given", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/customers", { name: "x" });
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const key = new Headers(init.headers).get("Idempotency-Key");
    expect(key).toBeTruthy();
    expect(key!.length).toBeGreaterThan(0);
  });

  it("apiPost honours an explicit Idempotency-Key (retry-safe replay)", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/customers", { name: "x" }, "my-fixed-key");
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(new Headers(init.headers).get("Idempotency-Key")).toBe("my-fixed-key");
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
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/auth/login");
    expect(new Headers(init.headers).get("Authorization")).toBeNull(); // login is unauthenticated
  });

  it("logout clears local tokens even when the server revoke fails", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "revoke failed" }, 500));
    await expect(logout()).resolves.toBeUndefined(); // best-effort, does not throw
    expect(loadTokens()).toBeNull();
  });
});
