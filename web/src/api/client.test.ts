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
  stepUp,
  STEP_UP_HEADER,
  ApiError,
  setOnUnauthenticated,
  setOnTokensChanged,
  getLastTraceId,
} from "./client";
import { getAccessToken, setAccessToken, clearAccessToken } from "../auth/tokenStore";
import { bindAccount, clearBoundAccount, getBoundAccountId } from "../auth/tokenStore";

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

// executeRefresh refuses a token whose account_id differs from the tab's own.
//
// #532 round 8 — mint as base64URL, like the server does: the standard-btoa
// fixture was the reason claims.ts's base64url translation looked deletable in
// round 7 (a test-minted payload never contains the URL-safe characters). A
// payload whose base64 form contains "+" (one 0xff byte does: …"//w=…")
// carries "-" in its base64url form, so deleting the translation line in
// claims.ts reddens the same-farm rotation test below, which mints such a
// token for BOTH the stored and the refreshed position.
function b64url(input: string): string {
  return btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
function jwtPayloadUrl(claims: Record<string, unknown>): string {
  return b64url(JSON.stringify(claims));
}
function jwtWithAccountId(accountId: string, marker: string, withPlus = false): string {
  return `hdr.${jwtPayloadUrl({ sub: `user-${marker}${withPlus ? "\xff" : ""}`, account_id: accountId, exp: 9999999999 })}.sig`;
}

// The access-token body login/refresh now return (no refresh token).
function accessResponse(accessToken: string, status = 200): Response {
  return jsonResponse({ accessToken, accessTokenExpiry: FUTURE }, status);
}

// #532 round 8 — the cross-farm guard fails closed on any token it cannot
// attribute to a farm. A plain-string token ("at2", "newLoginToken", …) is
// UNATTRIBUTABLE, so a refresh that returns one is REFUSED, not adopted — and
// the #310 supersession paths below (which resolve a refresh gate with such a
// token and then assert the discard) would never run. These helpers mint the
// same markers WITH an account claim, so the guard adopts (the tab is
// unbound) and the supersession logic under test actually executes.
function claimfulToken(marker: string): string {
  return jwtWithAccountId("acct-" + marker.split("-")[0], marker);
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
let onUnauth: ReturnType<typeof vi.fn<() => void>>;
let onTokens: ReturnType<typeof vi.fn<() => void>>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  onUnauth = vi.fn();
  onTokens = vi.fn();
  setOnUnauthenticated(onUnauth);
  setOnTokensChanged(onTokens);
  // #532 — the cross-farm guard fails closed on any token it cannot attribute
  // to a farm, so a token that has no account claim at all (like this
  // placeholder) only ever exercises the REFRESH-failure paths in this file.
  // The round-7 mutation (delete the null-claim throw) reddens the dedicated
  // claim-less test below, not these.
  // #532 — the cross-farm guard fails closed on any token it cannot attribute
  // to a farm, so a claim-less placeholder only ever exercises the
  // REFRESH-failure paths in this file. Deleting the null-claim throw in
  // executeRefresh reddens the dedicated claim-less test below (the tab is
  // unbound, so an unattributable token would be adopted and the refresh
  // would succeed) — these tests stay green either way by design.
  setAccessToken("at1");
  clearBoundAccount(); // #532 — the farm binding is module state too; a bound
  // leftover would silently flip a "cold restore" test into a "bound tab" test.
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnUnauthenticated(null);
  setOnTokensChanged(null);
  clearAccessToken();
  clearBoundAccount();
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

  it("preserves a credential-revocation reason from a failed silent refresh", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(jsonResponse({
      title: "Auth.CredentialsSuperseded",
      detail: "Your credentials changed.",
    }, 401));

    await expect(apiGet("/stock")).rejects.toMatchObject({ status: 401 });

    expect(onUnauth).toHaveBeenCalledWith("Auth.CredentialsSuperseded");
  });

  it("preserves a credential-revocation reason during load-time session restore", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(jsonResponse({
      title: "Auth.AccountDisabled",
      detail: "Your account has been disabled.",
    }, 401));

    await expect(restoreSession()).resolves.toBe(false);

    expect(onUnauth).toHaveBeenCalledWith("Auth.AccountDisabled");
  });

  it("uses the refreshed token when the silent refresh succeeds", async () => {
    clearAccessToken();
    fetchMock
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // silent refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // the request, now authed
    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(authOf(callsTo(fetchMock, "/stock")[0])).toBe(`Bearer ${claimfulToken("at2")}`);
  });
});

describe("apiFetch — unrecoverable session reason", () => {
  it("passes the original protected-request 401 title to auth teardown after refresh fails", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "Auth.CredentialsSuperseded" }, 401))
      .mockResolvedValueOnce(jsonResponse({ title: "Identity.InvalidRefreshToken" }, 401));

    await expect(apiGet("/stock")).rejects.toMatchObject({ title: "Auth.CredentialsSuperseded" });
    expect(onUnauth).toHaveBeenCalledWith("Auth.CredentialsSuperseded");
  });
});

// #217 — every request mints a W3C traceparent so a browser action correlates
// with the API's request log and spans (#214). The client remembers the last
// trace id so a crash report can join the failed screen's server-side story.
describe("traceparent correlation", () => {
  const TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;

  it("attaches a spec-shaped traceparent header to every JSON request", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ ok: true }));
    await apiGet("/stock");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "traceparent")).toMatch(TRACEPARENT);
  });

  it("attaches a traceparent to blob downloads too", async () => {
    // String body, not `new Blob(...)`: under vitest's jsdom env `Blob` is
    // jsdom's (no .stream()) while `Response` is undici's, and whether that
    // mix works depends on the Node version — it broke on CI's Node.
    fetchMock.mockResolvedValueOnce(new Response("x", { status: 200 }));
    await apiGetBlob("/export/eggs.csv");
    expect(headerOf(fetchMock.mock.calls[0] as Call, "traceparent")).toMatch(TRACEPARENT);
  });

  it("mints a fresh trace id per request and exposes the last one", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ ok: true }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));
    await apiGet("/stock");
    const first = headerOf(fetchMock.mock.calls[0] as Call, "traceparent")!;
    await apiGet("/flocks");
    const second = headerOf(fetchMock.mock.calls[1] as Call, "traceparent")!;

    expect(second).not.toBe(first);
    expect(getLastTraceId()).toBe(second.split("-")[1]);
  });

  it("remembers the trace id even when the request fails — that is the one worth reporting", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "boom" }, 500));
    await expect(apiGet("/stock")).rejects.toBeInstanceOf(ApiError);
    const sent = headerOf(fetchMock.mock.calls[0] as Call, "traceparent")!;
    expect(getLastTraceId()).toBe(sent.split("-")[1]);
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

  it("maps a known errorCode to its catalog message; an uncoded field (omitted from errorCodes, the real API shape) keeps English", async () => {
    // The real API OMITS a wholly-uncoded field from errorCodes (ValidationResponse.cs),
    // so `name` has no errorCodes entry at all — it must keep its English message.
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        {
          title: "Validation",
          errors: { language: ["Language must be a 2–8 letter code."], name: ["required"] },
          errorCodes: { language: ["Me.Language.Format"] },
        },
        400,
      ),
    );
    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    // Exact match (not just toContain): the catalog string adds ", for example
    // 'en'." — absent from the server's plain English — so this fails pre-fix
    // (plain flatten yields "Language must be a 2–8 letter code. required").
    expect((err as ApiError).message).toBe(
      "Language must be a 2–8 letter code, for example 'en'. required",
    );
  });

  it("falls back to the English message for an UNKNOWN code (no catalog key)", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        {
          errors: { language: ["some english message"] },
          errorCodes: { language: ["Not.A.Real.Code"] },
        },
        400,
      ),
    );
    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    // defaultValue is the server English message → unknown code degrades to it.
    expect((err as ApiError).message).toBe("some english message");
  });

  it("keeps the plain flattened English message when there are no errorCodes", async () => {
    // The pre-#182 contract, unchanged.
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ errors: { name: ["required"], qty: ["too big", "nan"] } }, 400),
    );
    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).message).toBe("required too big nan");
  });
});

describe("apiFetch — transparent refresh", () => {
  it("on 401, refreshes once and retries the original request with the new token", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // refresh
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
    expect(authOf(calls[2])).toBe(`Bearer ${claimfulToken("at2")}`);
    expect(onTokens).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBe(claimfulToken("at2"));
  });

  it("replays a write across a refresh with the SAME idempotency key and body", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original POST
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // refresh
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
    expect(authOf(retry)).toBe(`Bearer ${claimfulToken("at2")}`); // only the bearer changed
  });

  it("shares a single in-flight refresh across concurrent 401s (single-flight)", async () => {
    const gate = deferred<void>();
    let refreshes = 0;
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url.endsWith("/auth/refresh")) {
        refreshes += 1;
        if (refreshes > 1) throw new Error("a second refresh must not start while one is in flight");
        await gate.promise; // hold open until both 401s have parked on this refresh
        return accessResponse(claimfulToken("at2"));
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
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-cyc", "at2")))
      .mockResolvedValueOnce(jsonResponse({ ok: 1 })) // cycle 1 retry
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // cycle 2 original
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-cyc", "at3")))
      .mockResolvedValueOnce(jsonResponse({ ok: 2 })); // cycle 2 retry

    await apiGet("/a");
    await apiGet("/b");
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2); // a stuck latch would only refresh once
    expect(getAccessToken()).toBe(jwtWithAccountId("acct-cyc", "at3"));
  });
});

// #532 — the refresh cookie is per-ORIGIN (one per browser, last login wins)
// while the token store is per-TAB. A refresh in an old tab can therefore
// return a session for a DIFFERENT farm than the one this tab was operating as
// (another tab logged in elsewhere). Adopting it would let the retry replay
// this tab's pending request — body and all — against the other farm.


describe("executeRefresh — cross-farm adoption guard (#532)", () => {
  it("refuses a refreshed token from a DIFFERENT farm: no retry, session torn down", async () => {
    // The tab's binding is what the guard compares against (round 6 derived it
    // from the stored token and was a one-shot — see below). Bind it explicitly.
    bindAccount("acct-A");
    setAccessToken(jwtWithAccountId("acct-A", "tab"));
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(
        accessResponse(jwtWithAccountId("acct-B", "other-farm")),
      ); // refresh returns farm B's session

    const err = await apiGet("/stock").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    // The original request was NOT retried against the other farm. The extra
    // call is the refusal's own cookie revoke: the refused response rotated a
    // refresh cookie that a teardown cannot reach, so a reload would otherwise
    // restore the session this tab just refused (see #310/#393).
    expect(callsTo(fetchMock, "/stock")).toHaveLength(1);
    // #547 — no revoke: the refusal means the cookie belongs to ANOTHER farm's
    // tab, and revoking it would sign out a tab that did nothing wrong (#310).
    // Refusal tears down THIS tab only.
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(0);
    // This tab's session was torn down: token cleared, sent to login.
    expect(getAccessToken()).toBeNull();
    expect(onUnauth).toHaveBeenCalledTimes(1);
    expect(onTokens).not.toHaveBeenCalled(); // the other farm's token was never adopted
  });

  it("adopts and retries normally when the refreshed token carries the SAME farm", async () => {
    bindAccount("acct-A");
    // withPlus: the 0xff byte makes the payload's base64 contain "+", i.e. the
    // base64url form carries "-". This is the test that reddens if the
    // base64url translation line in claims.ts is deleted: without it the
    // account claim would not decode, the guard would refuse, and the retry
    // would never happen. (Round 7 found that line deletable precisely
    // because the btoa fixture never minted one.)
    setAccessToken(jwtWithAccountId("acct-A", "tab", true));
    const rotated = jwtWithAccountId("acct-A", "rotated", true);
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(rotated)) // refresh rotates within the same farm
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // the retry

    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    const retried = callsTo(fetchMock, "/stock")[1];
    expect(authOf(retried)).toBe(`Bearer ${rotated}`);
    expect(getAccessToken()).toBe(rotated);
    expect(onUnauth).not.toHaveBeenCalled();
    expect(onTokens).toHaveBeenCalledTimes(1);
  });

  it("still allows adoption on a cold restore (no prior token in this tab), and binds", async () => {
    clearAccessToken(); // the legitimate cold-restore path: nothing to compare against
    expect(getBoundAccountId()).toBeNull(); // ...and no binding — genuinely unbound
    fetchMock
      .mockResolvedValueOnce(
        accessResponse(jwtWithAccountId("acct-B", "restored")),
      ) // the silent refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // the request

    const body = await apiGet<{ ok: boolean }>("/stock");
    expect(body).toEqual({ ok: true });
    expect(getAccessToken()).toBe(jwtWithAccountId("acct-B", "restored"));
    // The restored tab is bound after adopting, so it is unbound exactly once:
    // a later refresh for a DIFFERENT farm is now refused, not silently adopted.
    expect(getBoundAccountId()).toBe("acct-B");
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-C", "other"))); // a foreign farm now

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(onUnauth).toHaveBeenCalledTimes(1);
  });

  // THE round-7 P1, proven sequential: a refusal tears the session down, which
  // clears the stored token — the SAME state the cold-restore allowance trusts
  // when it derives the tab's farm from the token. With the binding surviving
  // teardown, a second request must still be refused and NOT retried on the
  // foreign farm. Round 6's guard was defeated by exactly this: two ordinary
  // concurrent requests, the first refused, the second's POST /eggs replayed on
  // farm B's bearer with farm A's body.
  it("a repeat refusal is still refused — the binding survives the first refusal's teardown", async () => {
    bindAccount("acct-A");
    setAccessToken(jwtWithAccountId("acct-A", "tab"));

    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // request 1 original
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-B", "other-farm"))); // refresh → farm B
    const first = await apiGet("/stock").catch((e: unknown) => e);

    // First refusal: torn down, no retry.
    expect(first).toBeInstanceOf(ApiError);
    expect((first as ApiError).status).toBe(401);
    expect(getAccessToken()).toBeNull(); // teardown cleared the store — the round-6 one-shot state
    expect(getBoundAccountId()).toBe("acct-A"); // ...but the binding survived it

    // The second request: an empty store plus a binding is NOT a cold restore.
    fetchMock
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-B", "other-farm-again"))); // refresh again
    const second = await apiPost<{ id?: string }>("/eggs", { body: "farm-A-body" }, "repeat-key").catch(
      (e: unknown) => e,
    );

    expect(second).toBeInstanceOf(ApiError);
    expect((second as ApiError).status).toBe(401);
    // The second refresh was refused: /eggs was never sent on farm B's bearer.
    expect(callsTo(fetchMock, "/eggs")).toHaveLength(0);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2);
    expect(getAccessToken()).toBeNull();
  });

  // Round 7 adopted a token with no account_id, and the literal string
  // "not-a-jwt", because the guard trusted a null on the REFRESHED token. Both
  // are refused now: a token we cannot attribute to a farm is never adopted.
  it("refuses a refreshed token whose payload lacks an account_id claim", async () => {
    // UNBOUND tab: the null-claim throw is the ONLY check that fires here.
    // A bound tab would be caught by the mismatch check instead, which would
    // make the null-claim throw untestable (deleting it leaves this green).
    setAccessToken(jwtWithAccountId("acct-A", "tab"));
    const noAccount = `hdr.${jwtPayloadUrl({ sub: "user-x", exp: 9999999999 })}.sig`;
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(noAccount)); // refresh: no account claim

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(callsTo(fetchMock, "/stock")).toHaveLength(1); // no retry on the unattributable token
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(0); // #547 — a refusal never revokes the other farm's cookie
    expect(getAccessToken()).toBeNull();
    expect(onUnauth).toHaveBeenCalledTimes(1);
  });

  it("refuses an unparseable refreshed token ('not-a-jwt')", async () => {
    // UNBOUND tab: same reasoning as the null-claim test above.
    setAccessToken(jwtWithAccountId("acct-A", "tab"));
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse("not-a-jwt")); // refresh: garbage

    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(callsTo(fetchMock, "/stock")).toHaveLength(1); // no retry on the unparseable token
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(0); // #547 — a refusal never revokes the other farm's cookie
    expect(getAccessToken()).toBeNull();
    expect(onUnauth).toHaveBeenCalledTimes(1);
  });
});

// #547 — the SERVER-side half of the cross-farm guard: the tab tells
// /auth/refresh which farm it expects (X-Cluckwork-Account) and the server
// compares it against the stored token's AccountId before rotating. These
// tests pin the client's half of the contract: the header rides along when
// the tab is bound, is omitted when unbound, and an Auth.SessionChanged 401
// tears down THIS tab only — never a revoke of the other farm's cookie.

describe("executeRefresh — expected-account header (#547)", () => {
  it("sends X-Cluckwork-Account when the tab is bound", async () => {
    bindAccount("acct-A");
    setAccessToken(jwtWithAccountId("acct-A", "tab"));
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-A", "rotated"))) // refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // retry

    await apiGet("/stock");

    const refresh = callsTo(fetchMock, "/auth/refresh")[0];
    expect(headerOf(refresh, "X-Cluckwork-Account")).toBe("acct-A");
    expect(headerOf(refresh, CSRF)).toBe("1"); // the CSRF header still rides too
  });

  it("omits X-Cluckwork-Account when the tab is unbound (the bootstrap path)", async () => {
    // Cold restore: the in-memory store is EMPTY, so the refresh is the silent
    // bootstrap refresh (currentAccessToken → refreshTokens), not a 401 retry.
    clearAccessToken();
    expect(getBoundAccountId()).toBeNull();
    fetchMock
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-B", "restored"))) // the silent refresh
      .mockResolvedValueOnce(jsonResponse({ ok: true })); // the request, on the restored token

    await apiGet("/stock");

    expect(getAccessToken()).toBe(jwtWithAccountId("acct-B", "restored"));
    const refresh = callsTo(fetchMock, "/auth/refresh")[0];
    expect(headerOf(refresh, "X-Cluckwork-Account")).toBeNull(); // unbound → no expectation
  });

  it("an Auth.SessionChanged 401 tears down THIS tab only — no revoke of the other farm's cookie", async () => {
    // Empty store: the refresh is the SILENT bootstrap refresh
    // (currentAccessToken → refreshTokens), which surfaces the refresh's own
    // error — so the Auth.SessionChanged title reaches the caller directly.
    bindAccount("acct-A");
    clearAccessToken(); // the binding survives: this tab still knows its farm
    expect(getBoundAccountId()).toBe("acct-A");
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ title: "Auth.SessionChanged", detail: "This session now belongs to a different farm." }, 401),
    ); // the server refused: the cookie belongs to another farm

    const err = await apiGet("/stock").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).title).toBe("NoSession"); // currentAccessToken wraps it
    // This tab is torn down and re-bootstraps…
    expect(getAccessToken()).toBeNull();
    expect(getBoundAccountId()).toBeNull();
    // …because the REFRESH answered Auth.SessionChanged. It reaches the
    // teardown twice — executeRefresh's own branch AND currentAccessToken's
    // catch, which fires onUnauthenticated for any non-stale refresh failure
    // (the pre-existing double-navigation pattern for a silent-refresh
    // failure). Both carry the REFRESH's title, never the wrap's.
    expect(onUnauth).toHaveBeenCalledTimes(2);
    expect(onUnauth).toHaveBeenNthCalledWith(1, "Auth.SessionChanged");
    expect(onUnauth).toHaveBeenNthCalledWith(2, "Auth.SessionChanged");
    // …and the other farm's cookie is deliberately left alone (#310).
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(0);
    expect(callsTo(fetchMock, "/stock")).toHaveLength(0); // no request was ever sent — no token
  });
});

describe("#547 — login binds the tab to its farm (the header's source)", () => {
  it("a login stores the token AND binds the tab, so its next refresh sends the header — and a different-farm refresh is refused", async () => {
    clearAccessToken();
    expect(getBoundAccountId()).toBeNull();
    const loggedIn = jwtWithAccountId("acct-A", "fresh-login");
    fetchMock.mockResolvedValueOnce(accessResponse(loggedIn)); // the login itself
    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" });

    // THE binding the cross-farm guard depends on: derived from the login
    // token's account claim, not set by the test. Deleting the bindAccount
    // call in login() makes this null and the next assertions red.
    expect(getBoundAccountId()).toBe("acct-A");

    // A refresh for ANOTHER farm is now refused (client-side guard — the
    // server-side check is the same comparison over HTTP): no retry, teardown,
    // no revoke of the other farm's cookie.
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "expired" }, 401)) // original
      .mockResolvedValueOnce(accessResponse(jwtWithAccountId("acct-B", "other-farm")));
    const err = await apiGet("/stock").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect(callsTo(fetchMock, "/stock")).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(0);
    // The header the server check relies on was actually sent by the refresh
    // above: without the login-time binding, the server would see no
    // expectation and the client-side refusal could not have fired either.
    expect(headerOf(callsTo(fetchMock, "/auth/refresh")[0], "X-Cluckwork-Account")).toBe("acct-A");
  });
});

describe("#547 — cross-tab bootstrap broadcast", () => {
  // The module creates its channel at import time, so this runs after the
  // module loaded: in this DOM BroadcastChannel exists (jsdom), and a second
  // channel on the same name receives its messages.
  it("a login posts the new account to the shared channel (other tabs re-bootstrap from it)", async () => {
    // The module created its channel at import time; a second channel with the
    // same name receives its posts, so this proves the announcement without
    // reaching into the module. (jsdom provides BroadcastChannel; the module
    // guards for its absence and would simply skip the post otherwise.)
    const received: unknown[] = [];
    const receiver = new BroadcastChannel("cluckwork.session");
    receiver.onmessage = (event: MessageEvent) => received.push(event.data);

    const loggedIn = jwtWithAccountId("acct-A", "fresh-login");
    fetchMock.mockResolvedValueOnce(accessResponse(loggedIn));
    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" });

    // BroadcastChannel delivery is async even in-process: drain one macrotask.
    await drain();

    expect(received).toEqual([{ type: "session-bootstrap", accountId: "acct-A" }]);
    receiver.close();
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
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // refresh ok
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
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // refresh ok
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

  // #308 — apiPost/apiPut's optional 4th param is how CreateUser/SetUserPassword
  // attach the step-up grant. Both sides of the boundary: present when a caller
  // passes one, absent otherwise (every other write in the app).
  it("apiPost attaches an extra header (e.g. the step-up grant) when given one", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/users", { role: "Admin" }, "key", { [STEP_UP_HEADER]: "grant-abc" });
    expect(headerOf(fetchMock.mock.calls[0] as Call, STEP_UP_HEADER)).toBe("grant-abc");
  });

  it("apiPost carries no step-up header when none is given (every ordinary write)", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: "1" }));
    await apiPost("/customers", { name: "x" });
    expect(headerOf(fetchMock.mock.calls[0] as Call, STEP_UP_HEADER)).toBeNull();
  });

  it("apiPut attaches an extra header (e.g. the step-up grant) when given one", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await apiPut("/users/1/password", { newPassword: "x" }, "key", { [STEP_UP_HEADER]: "grant-def" });
    expect(headerOf(fetchMock.mock.calls[0] as Call, STEP_UP_HEADER)).toBe("grant-def");
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
      .mockResolvedValueOnce(accessResponse(claimfulToken("fresh")))               // the refresh
      .mockResolvedValueOnce(jsonResponse({ contentHash: "abc" })); // the retry

    await apiPutBytes("/account/logo", bytes, "image/png", "logo-key");

    const writes = callsTo(fetchMock, "/account/logo");
    expect(writes).toHaveLength(2);
    // A body consumed by the first attempt would make the retry upload nothing;
    // a Blob can be read again, which is why the retry is safe at all.
    expect(writes[1][1].body).toBe(bytes);
    expect(authOf(writes[1])).toBe(`Bearer ${claimfulToken("fresh")}`);
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
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))) // refresh
      .mockResolvedValueOnce(new Response("data", { status: 200 })); // retry

    const { blob } = await apiGetBlob("/export/all");
    expect(blob.size).toBe(4); // "data"
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(authOf(fetchMock.mock.calls[2] as Call)).toBe(`Bearer ${claimfulToken("at2")}`);
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
    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" });
    expect(getAccessToken()).toBe("atL");
    expect(localStorage.length).toBe(0); // never persisted
    expect(onTokens).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/login");
    expect(new Headers(init.headers).get("Authorization")).toBeNull(); // login is unauthenticated
  });

  it("logout revokes cookie-authenticated (CSRF header, no body), then clears the token", async () => {
    setAccessToken("at-logout");
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await logout();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as Call;
    expect(url).toBe("/api/v1/auth/logout");
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined(); // refresh token is in the cookie, not the body
    expect(headerOf(fetchMock.mock.calls[0] as Call, CSRF)).toBe("1");
    expect(getAccessToken()).toBeNull();
  });

  // #336 — the refresh cookie is per-ORIGIN (one per browser, last login wins)
  // while this token store is per-TAB, so the cookie can belong to a DIFFERENT
  // user than the one logging out. The bearer is what tells the server who
  // actually clicked logout, so their step-up grants are the ones revoked;
  // without it the server revokes the cookie owner's and leaves this user's
  // alive. It must be read BEFORE the synchronous clear, or it is always null.
  it("logout sends this tab's bearer, captured before the token is cleared", async () => {
    const token = `tok-${crypto.randomUUID()}`;
    setAccessToken(token);
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await logout();

    expect(authOf(fetchMock.mock.calls[0] as Call)).toBe(`Bearer ${token}`);
    expect(getAccessToken()).toBeNull();
  });

  // …but it stays OPTIONAL. The endpoint is AllowAnonymous, so a tab with no
  // token (or an expired one already dropped) must still fire the request and
  // end the session off the cookie alone — never send "Bearer null".
  it("logout omits the bearer entirely when this tab holds no access token", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await logout();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(authOf(fetchMock.mock.calls[0] as Call)).toBeNull();
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

  // #310 — local invalidation must win immediately, independent of the network.
  it("clears in-memory auth state synchronously, before the server call settles", async () => {
    const gate = deferred<Response>();
    fetchMock.mockReturnValueOnce(gate.promise); // server call hangs
    setAccessToken("live-token");

    const done = logout();
    await Promise.resolve(); // let logout()'s synchronous prefix run; network still pending
    expect(getAccessToken()).toBeNull();

    gate.resolve(new Response(null, { status: 204 }));
    await done;
  });

  // #310 — a full network rejection (not just a non-2xx response) must behave
  // the same as the existing 500 case above: local state wins regardless.
  it("clears the local token even when the server call rejects outright (network error)", async () => {
    setAccessToken("live-token");
    fetchMock.mockRejectedValueOnce(new TypeError("network down"));
    await expect(logout()).resolves.toBeUndefined();
    expect(getAccessToken()).toBeNull();
  });

  // #310 — the revoke failure must be observable (not silently swallowed) but
  // must never expose the token that was already cleared.
  it("reports a server-side revoke failure without leaking the token", async () => {
    const consoleErr = vi.spyOn(console, "error").mockImplementation(() => {});
    // Generated, not a literal: nothing secret-shaped belongs in source
    // (GitGuardian scans test files too), and a unique value makes the
    // "did not leak" assertion exact rather than coincidental.
    const token = `tok-${crypto.randomUUID()}`;
    setAccessToken(token);
    fetchMock.mockRejectedValueOnce(new TypeError("network down"));

    await expect(logout()).resolves.toBeUndefined();

    expect(getAccessToken()).toBeNull();
    expect(consoleErr).toHaveBeenCalledTimes(1);
    const logged = consoleErr.mock.calls[0].map((a) => String(a)).join(" ");
    expect(logged).not.toContain(token);
    consoleErr.mockRestore();
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

  it("refreshes and retries inside the cookie lock when the access token expired", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "Unauthorized" }, 401))
      .mockResolvedValueOnce(accessResponse(claimfulToken("refreshed-access")))
      .mockResolvedValueOnce(accessResponse("password-change-access"));

    await changePassword({ currentPassword: "a", newPassword: "b" });

    const changes = callsTo(fetchMock, "/auth/change-password");
    expect(changes).toHaveLength(2);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    // The one logical write keeps its idempotency key across the auth retry.
    expect(headerOf(changes[1], "Idempotency-Key"))
      .toBe(headerOf(changes[0], "Idempotency-Key"));
    expect(authOf(changes[1])).toBe(`Bearer ${claimfulToken("refreshed-access")}`);
    expect(getAccessToken()).toBe("password-change-access");
  });
});

// #308 — step-up grant issuance. Consumed by UsersPage.tsx immediately before
// the one sensitive write it unlocks; this file only proves the transport
// contract (posts the password, authenticated, returns the grant as-is).
describe("stepUp (#308)", () => {
  it("posts the password to /auth/step-up as an authenticated write and returns the grant", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ token: "grant-abc", expiresAt: FUTURE }));
    const password = `Aa1!${crypto.randomUUID()}`;

    const grant = await stepUp(password);

    const call = fetchMock.mock.calls[0] as Call;
    expect(call[0]).toBe("/api/v1/auth/step-up");
    expect(call[1].method).toBe("POST");
    expect(authOf(call)).toBe("Bearer at1");
    expect(JSON.parse(String(call[1].body))).toEqual({ password });
    expect(grant).toEqual({ token: "grant-abc", expiresAt: FUTURE });
  });

  it("propagates a rejection (wrong current password) as an ApiError carrying the server's message", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ title: "Users.CurrentPasswordIncorrect", detail: "Current password is incorrect." }, 400));

    const err = await stepUp("wrong").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 400, message: "Current password is incorrect." });
  });

  // #336 review — the damage a 401 here would do is NOT the status the user
  // sees, it is the replay: apiFetch treats every 401 as an expired access
  // token, refreshes, and re-sends the SAME password. One typed password would
  // then reach the server's password check twice, so its five-attempt account
  // lockout would trip after three submissions and each attempt would burn a
  // refresh-token rotation. Pin the single request, not just the status.
  it("issues exactly one request for a rejected password — no refresh, no replay", async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ title: "Users.CurrentPasswordIncorrect", detail: "Current password is incorrect." }, 400));

    await expect(stepUp("wrong")).rejects.toThrow(ApiError);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(callsTo(fetchMock, "/auth/step-up")).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(0);
    // The session is untouched: no teardown, no navigate-to-login.
    expect(getAccessToken()).toBe("at1");
    expect(onUnauth).not.toHaveBeenCalled();
    expect(onTokens).not.toHaveBeenCalled();
  });

  // The other side of the boundary: a REAL 401 (the session truly expired) must
  // still take the refresh-and-retry path, so the fix above is a status
  // distinction and not a blanket opt-out of transparent refresh.
  it("still refreshes and retries when the session itself has expired (401)", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: "Unauthorized" }, 401))
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2")))
      .mockResolvedValueOnce(jsonResponse({ token: "grant-abc", expiresAt: FUTURE }));

    const grant = await stepUp("right");

    expect(grant).toEqual({ token: "grant-abc", expiresAt: FUTURE });
    expect(callsTo(fetchMock, "/auth/step-up")).toHaveLength(2);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
  });

  it("does not touch in-memory auth state — it is not a token-store writer", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ token: "grant-abc", expiresAt: FUTURE }));
    await stepUp("whatever");
    expect(getAccessToken()).toBe("at1"); // unchanged
    expect(onTokens).not.toHaveBeenCalled();
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
      url.endsWith("/auth/refresh") ? accessResponse(claimfulToken("at2")) : jsonResponse({ ok: true }),
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
    expect(getAccessToken()).toBe(claimfulToken("at2"));
  });

  it("serializes password change after an in-flight refresh so stale credentials cannot win last", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    const refreshGate = deferred<Response>();
    const changeGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/change-password")) return changeGate.promise;
      throw new Error(`unexpected fetch: ${url}`);
    });

    // The refresh proves the old credential epoch, then remains in flight. A
    // password change that overtakes it can commit E+1 first, only for the late
    // refresh response to overwrite both browser credentials with retired E.
    const refreshing = restoreSession();
    await drain();
    const changing = changePassword({ currentPassword: "a", newPassword: "b" });
    await drain();

    // Resolve in the dangerous order. Before serialization, change-password
    // commits first and the stale refresh wins last. With the shared lock, the
    // already-resolved password response is not requested until refresh exits.
    changeGate.resolve(accessResponse("password-change-token"));
    await drain();
    refreshGate.resolve(accessResponse(claimfulToken("stale-refresh-token")));

    await expect(refreshing).resolves.toBe(true);
    await expect(changing).resolves.toBeUndefined();
    expect(getAccessToken()).toBe("password-change-token");
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(1);
    expect(sutLockName(locks)).toBe("cluckwork.auth.refresh");
  });

  it("drops a queued password change before any request when logout supersedes it", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    // Another tab owns the cookie lock, so this tab's password change queues
    // without starting a request. Logout must make that queued operation inert.
    const otherTab = deferred<void>();
    locks.request("cluckwork.auth.refresh", () => otherTab.promise);
    fetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/auth/logout")) return new Response(null, { status: 204 });
      if (url.endsWith("/auth/refresh")) return accessResponse("post-logout-refresh");
      if (url.endsWith("/auth/change-password")) return accessResponse("post-logout-change");
      throw new Error(`unexpected fetch: ${url}`);
    });

    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((err: unknown) => err);
    await drain();
    await logout();
    otherTab.resolve();
    await changing;

    expect(getAccessToken()).toBeNull();
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(0);
    expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(0);
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(1);
  });

  it("drops a queued password change before it can write into a newer login", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    const otherTab = deferred<void>();
    locks.request("cluckwork.auth.refresh", () => otherTab.promise);
    fetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/auth/login")) return accessResponse(claimfulToken("new-login-token"));
      if (url.endsWith("/auth/change-password")) return accessResponse("stale-change-token");
      throw new Error(`unexpected fetch: ${url}`);
    });

    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((err: unknown) => err);
    await drain();
    await login({ farmCode: "default-farm", email: "new@session.test", password: `pw-${crypto.randomUUID()}` });
    otherTab.resolve();
    await changing;

    expect(getAccessToken()).toBe(claimfulToken("new-login-token"));
    expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(0);
  });

  it("drops a password change whose initial refresh is superseded by a newer login", async () => {
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return accessResponse(claimfulToken("new-login-token"));
      if (url.endsWith("/auth/change-password")) return accessResponse("stale-change-token");
      throw new Error(`unexpected fetch: ${url}`);
    });

    clearAccessToken();
    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((err: unknown) => err);
    await drain();

    // The form is already inside the cookie lock, obtaining an access token.
    // A newer login must prevent that old form from borrowing the new bearer
    // when the now-stale refresh finishes.
    await login({ farmCode: "default-farm", email: "new@session.test", password: `pw-${crypto.randomUUID()}` });
    refreshGate.resolve(accessResponse(claimfulToken("stale-refresh-token")));
    await changing;

    expect(getAccessToken()).toBe(claimfulToken("new-login-token"));
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(0);
  });

  it("never retries an in-flight password change against a newer login", async () => {
    const firstChange = deferred<Response>();
    let changeCalls = 0;
    fetchMock.mockImplementation(async (url: string) => {
      if (url.endsWith("/auth/change-password")) {
        changeCalls += 1;
        if (changeCalls === 1) return firstChange.promise;
        return accessResponse("stale-form-retry-token");
      }
      if (url.endsWith("/auth/login")) return accessResponse(claimfulToken("new-login-token"));
      if (url.endsWith("/auth/refresh")) return accessResponse(claimfulToken("new-login-refreshed"));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((err: unknown) => err);
    await drain();

    // Supersede the form while its first request is already on the wire, then
    // make that old request answer 401. Generic apiFetch behavior would refresh
    // the newer session and resend the old form body against it.
    await login({ farmCode: "default-farm", email: "new@session.test", password: `pw-${crypto.randomUUID()}` });
    firstChange.resolve(jsonResponse({ title: "Unauthorized" }, 401));
    await changing;

    expect(getAccessToken()).toBe(claimfulToken("new-login-token"));
    expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(0);
  });

  it("does not apply the refresh timeout to a non-replayable password change", async () => {
    vi.useFakeTimers();
    try {
      let changeSignal: AbortSignal | undefined;
      const changeGate = deferred<Response>();
      fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
        if (url.endsWith("/auth/change-password")) {
          changeSignal = init.signal ?? undefined;
          return changeGate.promise;
        }
        throw new Error(`unexpected fetch: ${url}`);
      });

      const changing = changePassword({ currentPassword: "a", newPassword: "b" });
      await vi.advanceTimersByTimeAsync(0);
      expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(1);

      // A slow committed response cannot be retried with the old password. The
      // 15-second refresh timeout therefore must not abort this mutation.
      await vi.advanceTimersByTimeAsync(20_000);
      const wasAborted = changeSignal?.aborted;

      changeGate.resolve(accessResponse("password-change-token"));
      await changing.catch(() => undefined);
      expect(wasAborted).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("still times out a refresh nested inside the unbounded password-change lock", async () => {
    vi.useFakeTimers();
    try {
      let refreshSignal: AbortSignal | undefined;
      let rejectRefresh!: (reason: unknown) => void;
      let refreshCalls = 0;
      fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
        if (url.endsWith("/auth/refresh")) {
          refreshCalls += 1;
          if (refreshCalls > 1) return accessResponse(claimfulToken("recovered-token"));
          refreshSignal = init.signal ?? undefined;
          return new Promise<Response>((_resolve, reject) => {
            rejectRefresh = reject;
            refreshSignal?.addEventListener("abort", () =>
              reject(new DOMException("timed out", "AbortError")),
            );
          });
        }
        if (url.endsWith("/auth/change-password")) return accessResponse("must-not-send");
        throw new Error(`unexpected fetch: ${url}`);
      });

      clearAccessToken();
      const changing = changePassword({ currentPassword: "a", newPassword: "b" })
        .catch((err: unknown) => err);
      await vi.advanceTimersByTimeAsync(0);
      expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);

      await vi.advanceTimersByTimeAsync(20_000);
      const wasAborted = refreshSignal?.aborted;
      // Cleanup for the red implementation, whose missing timeout leaves the
      // promise pending. This is a no-op after the fixed abort already rejected.
      rejectRefresh(new DOMException("test cleanup", "AbortError"));
      await changing;

      expect(wasAborted).toBe(true);
      expect(callsTo(fetchMock, "/auth/change-password")).toHaveLength(0);
      // The failed operation released both the local queue and Web Lock.
      await expect(restoreSession()).resolves.toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("still aborts an in-flight password change when the user explicitly logs out", async () => {
    const changeStarted = deferred<void>();
    let changeSignal: AbortSignal | undefined;
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url.endsWith("/auth/change-password")) {
        changeSignal = init.signal ?? undefined;
        changeStarted.resolve();
        return new Promise<Response>((_resolve, reject) =>
          changeSignal?.addEventListener("abort", () =>
            reject(new DOMException("logged out", "AbortError")),
          ),
        );
      }
      if (url.endsWith("/auth/logout")) return new Response(null, { status: 204 });
      throw new Error(`unexpected fetch: ${url}`);
    });

    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((err: unknown) => err);
    await changeStarted.promise;
    await logout();

    expect(changeSignal?.aborted).toBe(true);
    expect(await changing).toMatchObject({ name: "AbortError" });
    expect(getAccessToken()).toBeNull();
  });

  it("a lock held under a DIFFERENT name does not block refresh (fake is name-scoped, like the real API)", async () => {
    const locks = fakeLockManager();
    vi.stubGlobal("navigator", { locks });

    // Some unrelated feature holds a different lock — must NOT serialise refresh.
    const unrelated = deferred<void>();
    locks.request("some-other-feature", () => unrelated.promise);

    clearAccessToken();
    fetchMock.mockImplementation(async (url: string) =>
      url.endsWith("/auth/refresh") ? accessResponse(claimfulToken("at2")) : jsonResponse({ ok: true }),
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
      .mockResolvedValueOnce(accessResponse(claimfulToken("at2"))); // refresh 2 succeeds

    await expect(restoreSession()).resolves.toBe(false);
    await expect(restoreSession()).resolves.toBe(true);

    // A stuck latch would have refused the second refresh entirely.
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(2);
    expect(getAccessToken()).toBe(claimfulToken("at2"));
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
      url.endsWith("/auth/refresh") ? accessResponse(claimfulToken("at2")) : jsonResponse({ ok: true }),
    );

    const a = await apiGet<{ ok: boolean }>("/a");

    expect(a).toEqual({ ok: true });
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1); // still refreshes, just uncoordinated
    expect(getAccessToken()).toBe(claimfulToken("at2"));
  });
});

// #310 — login, bootstrap refresh, explicit refresh, and logout are one browser
// session state machine. A stale completion (one whose generation was superseded
// by a later login or an earlier logout) may never commit tokens or state.
describe("session generation (#310)", () => {
  it("commits a bootstrap refresh normally when nothing else races it (control)", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(accessResponse(claimfulToken("bootedToken")));

    await expect(restoreSession()).resolves.toBe(true);

    expect(getAccessToken()).toBe(claimfulToken("bootedToken"));
    expect(onTokens).toHaveBeenCalledTimes(1);
  });

  it("discards a bootstrap refresh that resolves after logout — the session does not resurrect", async () => {
    clearAccessToken();
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const restoring = restoreSession(); // bootstrap refresh in flight, held open
    await drain();
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(1);

    await logout(); // user logs out while the bootstrap refresh is still pending

    refreshGate.resolve(accessResponse(claimfulToken("resurrected-token"))); // the stale refresh finally answers
    await expect(restoring).resolves.toBe(false); // discarded — never reports a restored session

    expect(getAccessToken()).toBeNull(); // must stay logged out
    expect(onTokens).not.toHaveBeenCalled(); // the stale completion must not fire tokensChanged
    // #310 review — clearing the in-memory token is only half of it. That
    // response carried a rotated refresh cookie, which the browser applied
    // before any of our code ran, so the server session must be revoked too:
    // one revoke for the logout itself, a second for the cookie the discarded
    // refresh issued. Without it a reload walks straight back in.
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(2);
  });

  it("commits an explicit login normally when nothing else races it (control)", async () => {
    clearAccessToken();
    fetchMock.mockResolvedValueOnce(accessResponse("loggedInToken"));

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" });

    expect(getAccessToken()).toBe("loggedInToken");
    expect(onTokens).toHaveBeenCalledTimes(1);
  });

  it("does not let a late bootstrap refresh overwrite a newer explicit login", async () => {
    clearAccessToken();
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return Promise.resolve(accessResponse(claimfulToken("newLoginToken")));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const restoring = restoreSession(); // older bootstrap refresh in flight
    await drain();

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }); // a newer, explicit login completes first
    expect(getAccessToken()).toBe(claimfulToken("newLoginToken"));
    expect(onTokens).toHaveBeenCalledTimes(1);

    refreshGate.resolve(accessResponse(claimfulToken("staleBootstrapToken"))); // the old refresh answers late
    await expect(restoring).resolves.toBe(false); // discarded, not adopted

    expect(getAccessToken()).toBe(claimfulToken("newLoginToken")); // untouched by the stale refresh
    expect(onTokens).toHaveBeenCalledTimes(1); // still just the login's single notification
  });

  it("a late-FAILING obsolete refresh does not clear or corrupt a newer session", async () => {
    // Distinct from the success case above: here the stale refresh's own network
    // call fails (refresh token revoked) — the failure path must be gated too, or
    // it would clearAccessToken()/onUnauthenticated() and boot the fresh login.
    clearAccessToken();
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return Promise.resolve(accessResponse("freshLoginToken"));
      if (url.endsWith("/stock")) return Promise.resolve(jsonResponse({ ok: true }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    // apiGet with no in-memory token forces currentAccessToken() to park on a
    // silent refresh — the same refresh a bootstrap would trigger.
    const fetching = apiGet<{ ok: boolean }>("/stock").catch((e: unknown) => e);
    await drain();

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }); // supersedes the still-parked refresh
    expect(getAccessToken()).toBe("freshLoginToken");

    refreshGate.resolve(jsonResponse({ title: "refresh token revoked" }, 401)); // stale refresh FAILS late
    const settled = await fetching;

    expect(getAccessToken()).toBe("freshLoginToken"); // untouched by the stale failure
    expect(onUnauth).not.toHaveBeenCalled(); // must not tear down the fresh session
    // #310 review — the parked request itself must not surface the internal
    // discard marker. The login committed a valid token, so the request the
    // user is actually waiting on proceeds on it rather than failing with
    // "Discarded: superseded by…", which every screen would render verbatim.
    expect(settled).toEqual({ ok: true });
  });

  // #310 review — changePassword is a token-store writer too, and logout is
  // reachable from every screen, so it needs the same generation guard as
  // login/refresh; without it the response writes a fresh token back over a
  // session the user already ended.
  it("discards a change-password response that resolves after logout (#310)", async () => {
    const changeGate = deferred<Response>();
    setAccessToken(`tok-${crypto.randomUUID()}`);
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/change-password")) return changeGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(jsonResponse({}, 204));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const changing = changePassword({ currentPassword: "a", newPassword: "b" })
      .catch((e: unknown) => e);
    await drain();

    await logout();
    expect(getAccessToken()).toBeNull();

    changeGate.resolve(jsonResponse({ accessToken: "resurrected" }));
    await changing;

    expect(getAccessToken()).toBeNull(); // the ended session stays ended
  });

  it("commits a change-password response normally when nothing races it (control)", async () => {
    setAccessToken(`tok-${crypto.randomUUID()}`);
    fetchMock.mockResolvedValueOnce(accessResponse("at-after-change"));

    await changePassword({ currentPassword: "a", newPassword: "b" });

    expect(getAccessToken()).toBe("at-after-change");
  });

  it("discards a login that resolves after a concurrent logout — does not resurrect authentication", async () => {
    clearAccessToken();
    const loginGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/login")) return loginGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const loggingIn = login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }).catch((e: unknown) => e);
    await drain();

    await logout(); // fires while the login request is still in flight

    loginGate.resolve(accessResponse(claimfulToken("late-login-token")));
    const result = await loggingIn;
    expect(result).toBeInstanceOf(Error); // discarded, surfaced as a rejection — not silently accepted

    expect(getAccessToken()).toBeNull();
    expect(onTokens).not.toHaveBeenCalled();
    // The late login's Set-Cookie already reached the browser; revoke it, or a
    // reload restores the session the user just ended.
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(2);
  });

  // #310 review — a request parked on a refresh that a LOGOUT discarded must
  // surface a normal 401, not the internal StaleSessionError whose message
  // every screen would render verbatim. (The newer-login counterpart, where the
  // request proceeds on the fresh token, is asserted above.)
  it("surfaces a plain 401 — not the discard marker — when logout ended the session", async () => {
    clearAccessToken();
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const fetching = apiGet("/stock").catch((e: unknown) => e);
    await drain();

    await logout();
    refreshGate.resolve(accessResponse(claimfulToken("discarded-token")));

    const settled = await fetching;
    expect(settled).toBeInstanceOf(ApiError);
    expect(settled).toMatchObject({ status: 401 });
  });

  // The two outcomes again for apiFetch's 401-RETRY branch specifically. The
  // test above enters through currentAccessToken (empty token store); this one
  // starts authenticated, 401s, and is superseded mid-retry — a separate branch.
  it("retries a request on the newer login's token when the retry refresh is superseded", async () => {
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/stock") && getAccessToken() === claimfulToken("newLoginToken"))
        return Promise.resolve(jsonResponse({ value: 7 }));
      if (url.endsWith("/stock")) return Promise.resolve(jsonResponse({ title: "expired" }, 401));
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return Promise.resolve(accessResponse(claimfulToken("newLoginToken")));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const fetching = apiGet<{ value: number }>("/stock");
    await drain();

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" });
    refreshGate.resolve(accessResponse(claimfulToken("stale-token")));

    expect(await fetching).toEqual({ value: 7 });
  });

  it("surfaces the original 401 when logout supersedes a retry refresh", async () => {
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/stock")) return Promise.resolve(jsonResponse({ title: "expired" }, 401));
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const fetching = apiGet("/stock").catch((e: unknown) => e);
    await drain();

    await logout();
    refreshGate.resolve(accessResponse(claimfulToken("discarded-token")));

    const settled = await fetching;
    expect(settled).toBeInstanceOf(ApiError);
    expect(settled).toMatchObject({ status: 401 });
  });

  // The same two outcomes for the blob path, whose 401-retry is a separate
  // branch from apiFetch's.
  it("retries a download on the newer login's token when a refresh is superseded", async () => {
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/export/all") && getAccessToken() === claimfulToken("newLoginToken"))
        return Promise.resolve(new Response("data", { status: 200 }));
      if (url.endsWith("/export/all")) return Promise.resolve(jsonResponse({ title: "expired" }, 401));
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return Promise.resolve(accessResponse(claimfulToken("newLoginToken")));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const downloading = apiGetBlob("/export/all");
    await drain();

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }); // supersedes the parked refresh
    refreshGate.resolve(accessResponse(claimfulToken("stale-token")));

    const { blob } = await downloading;
    expect(blob.size).toBe(4); // "data" — served on the live login's token
  });

  it("surfaces the original 401 on a download when logout ended the session", async () => {
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/export/all")) return Promise.resolve(jsonResponse({ title: "expired" }, 401));
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const downloading = apiGetBlob("/export/all").catch((e: unknown) => e);
    await drain();

    await logout();
    refreshGate.resolve(accessResponse(claimfulToken("discarded-token")));

    const settled = await downloading;
    expect(settled).toBeInstanceOf(ApiError);
    expect(settled).toMatchObject({ status: 401 });
  });

  // #393 — corrected from "does not revoke the cookie when a newer login
  // superseded the flight". That title encoded a false premise: "the cookie in
  // the browser belongs to that live login" is not something a present access
  // token can prove. A successful /auth/refresh response applies its
  // Set-Cookie the instant the browser parses it, unconditionally, before any
  // generation-check JS runs — whether the PREVIOUS user's rotated cookie or
  // the new login's cookie ends up "current" is real network arrival order,
  // invisible to JS, and the HttpOnly cookie can't be read back to check
  // either. Skipping the revoke here (the old behavior) could leave the
  // previous user's cookie live in the browser, silently, with the new
  // session's correct in-memory token masking it until reload.
  it("still revokes the stale refresh's cookie even though a newer login already set a token", async () => {
    clearAccessToken();
    const refreshGate = deferred<Response>();
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith("/auth/refresh")) return refreshGate.promise;
      if (url.endsWith("/auth/login")) return Promise.resolve(accessResponse(claimfulToken("newLoginToken")));
      if (url.endsWith("/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      throw new Error(`unexpected fetch: ${url}`);
    });

    const restoring = restoreSession();
    await drain();

    await login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }); // supersedes the parked refresh
    refreshGate.resolve(accessResponse(claimfulToken("stale-refresh-token")));
    await restoring;

    expect(getAccessToken()).toBe(claimfulToken("newLoginToken")); // the live login survives
    // The stale refresh's response DID rotate a cookie in the browser — that
    // must be revoked regardless, or a reload risks walking back into the
    // WRONG session depending on which Set-Cookie the browser actually kept.
    expect(callsTo(fetchMock, "/auth/logout")).toHaveLength(1);
  });
});
