import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiGetBlob, changePassword, confirmTwoFactor, isSignedOut, login, logout, restoreSession, setOnTokensChanged, setOnUnauthenticated, STEP_UP_HEADER, turnOffTwoFactor } from "./client";
import { refreshTokens } from "./refresh";
import { REFRESH_TIMEOUT_MS } from "./authCookieLock";
import { getTwoFactorStatus, regenerateRecoveryCodes, startTwoFactor } from "./cluckwork";
import { clearAccessToken, clearBoundAccount, getAccessToken, getBoundAccountId, setAccessToken } from "../auth/tokenStore";
import { freshCode } from "../test/codes";
import { StaleSessionError } from "./sessionState";

// #1277 — the two-factor routes. Confirm and turn-off hand this tab a fresh
// pair; every write's stale-session 401 goes back to the caller unsent again.
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const b64url = (s: string) => btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const token = (accountId: string) =>
  `hdr.${b64url(JSON.stringify({ sub: crypto.randomUUID(), account_id: accountId, exp: 9999999999 }))}.sig`;
const pair = (accessToken: string, extra: object = {}) =>
  json({ accessToken, accessTokenExpiry: "2099-01-01T00:00:00Z", ...extra });
const superseded = () => json({ title: "Auth.CredentialsSuperseded", detail: "Your credentials changed." }, 401);

type Call = [string, RequestInit];
let fetchMock: ReturnType<typeof vi.fn>;
let onTokens: ReturnType<typeof vi.fn<() => void>>;
let onUnauth: ReturnType<typeof vi.fn<() => void>>;
const calls = () => fetchMock.mock.calls as Call[];
const to = (suffix: string) => calls().filter(([url]) => url.endsWith(suffix));
const header = (call: Call, name: string) => new Headers(call[1].headers).get(name);

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  onTokens = vi.fn();
  onUnauth = vi.fn();
  setOnTokensChanged(onTokens);
  setOnUnauthenticated(onUnauth);
  setAccessToken("at1");
  clearBoundAccount();
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnTokensChanged(null);
  setOnUnauthenticated(null);
  clearAccessToken();
  clearBoundAccount();
});

describe("confirmTwoFactor", () => {
  it("sends the code with a key, adopts the fresh pair and returns the codes", async () => {
    const fresh = token("acct-A");
    const codes = [crypto.randomUUID(), crypto.randomUUID()];
    fetchMock.mockResolvedValueOnce(pair(fresh, { recoveryCodes: codes }));
    const code = freshCode();

    await expect(confirmTwoFactor(code)).resolves.toEqual({ recoveryCodes: codes });

    const [call] = to("/auth/two-factor/confirm");
    expect(call![1].method).toBe("POST");
    expect(JSON.parse(String(call![1].body))).toEqual({ code });
    expect(header(call!, "Idempotency-Key")).toMatch(/^[0-9a-f-]{36}$/);
    expect(header(call!, "Authorization")).toBe("Bearer at1");
    expect(getAccessToken()).toBe(fresh);
    expect(getBoundAccountId()).toBe("acct-A");
    expect(onTokens).toHaveBeenCalled();
  });

  it("adopts a replacement's pair, which carries no codes", async () => {
    const fresh = token("acct-B");
    fetchMock.mockResolvedValueOnce(pair(fresh));

    await expect(confirmTwoFactor(freshCode())).resolves.toEqual({ recoveryCodes: undefined });
    expect(getAccessToken()).toBe(fresh);
  });

  it("keeps attemptsLeft on a wrong code's error", async () => {
    fetchMock.mockResolvedValueOnce(json({ title: "Auth.EnrolmentCodeIncorrect", attemptsLeft: 3 }, 400));

    await expect(confirmTwoFactor(freshCode()))
      .rejects.toMatchObject({ status: 400, title: "Auth.EnrolmentCodeIncorrect", extensions: { attemptsLeft: 3 } });
    expect(getAccessToken()).toBe("at1");
  });
});

describe("turnOffTwoFactor", () => {
  it("sends DELETE with the grant and a key, and adopts the 200's fresh pair", async () => {
    const fresh = token("acct-C");
    fetchMock.mockResolvedValueOnce(pair(fresh));
    const grant = crypto.randomUUID();

    await turnOffTwoFactor(grant);

    const [call] = to("/auth/two-factor");
    expect(call![1].method).toBe("DELETE");
    expect(header(call!, STEP_UP_HEADER)).toBe(grant);
    expect(header(call!, "Idempotency-Key")).toMatch(/^[0-9a-f-]{36}$/);
    expect(getAccessToken()).toBe(fresh);
    expect(onTokens).toHaveBeenCalled();
  });

  it("adopts nothing on a 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await turnOffTwoFactor(crypto.randomUUID());

    expect(getAccessToken()).toBe("at1");
    expect(onTokens).not.toHaveBeenCalled();
  });

  // A 204 rotates nothing, but a session that ended while it was in flight
  // must not be told its two-factor was turned off.
  it("does not report a 204 that lands after a logout", async () => {
    let answer!: (res: Response) => void;
    fetchMock
      .mockReturnValueOnce(new Promise<Response>((resolve) => { answer = resolve; }))
      .mockResolvedValue(new Response(null, { status: 204 }));

    const turnOff = turnOffTwoFactor(crypto.randomUUID());
    await vi.waitFor(() => expect(to("/auth/two-factor")).toHaveLength(1));
    await logout();
    answer(new Response(null, { status: 204 }));

    await expect(turnOff).rejects.toBeInstanceOf(StaleSessionError);
  });
});

// Only turn-off answers 204. The other two rotate the session on every
// success, so a 204 from them is a broken answer, not a quiet success.
describe("a 204 where a fresh pair is due", () => {
  it.each([
    ["confirm", () => confirmTwoFactor(freshCode())],
    ["change-password", () => changePassword({ currentPassword: crypto.randomUUID(), newPassword: crypto.randomUUID() })],
  ])("from %s is an error", async (_, write) => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(write()).rejects.toThrow(/answered 204/);
    expect(getAccessToken()).toBe("at1");
  });
});

// #1280 review — the 401 means the session went stale after the grant was
// spent. A refresh-and-resend would act on a state the user never confirmed.
describe("a stale session's 401 Auth.CredentialsSuperseded", () => {
  const writes: [string, string, () => Promise<unknown>][] = [
    ["turn-off", "/auth/two-factor", () => turnOffTwoFactor(crypto.randomUUID())],
    ["confirm", "/auth/two-factor/confirm", () => confirmTwoFactor(freshCode())],
    ["start", "/auth/two-factor/start", () => startTwoFactor(crypto.randomUUID())],
    ["regenerate", "/auth/two-factor/recovery-codes", () => regenerateRecoveryCodes(crypto.randomUUID())],
  ];

  it.each(writes)("on %s is sent once, with no refresh, and does not end the session", async (_, path, write) => {
    fetchMock.mockResolvedValueOnce(superseded()).mockResolvedValue(pair(token("acct-D")));

    await expect(write()).rejects.toMatchObject({ status: 401, title: "Auth.CredentialsSuperseded" });

    expect(calls().map(([url]) => url)).toEqual([`/api/v1${path}`]);
    expect(onUnauth).not.toHaveBeenCalled();
    expect(getAccessToken()).toBe("at1");
  });

  it("on the status read still refreshes and retries, like any read", async () => {
    fetchMock
      .mockResolvedValueOnce(superseded())
      .mockResolvedValueOnce(pair(token("acct-E")))
      .mockResolvedValueOnce(json({ enabled: true, recoveryCodesLeft: 10 }));

    await expect(getTwoFactorStatus()).resolves.toEqual({ enabled: true, recoveryCodesLeft: 10 });
    expect(to("/auth/refresh")).toHaveLength(1);
  });
});

// A plain 401 is an expired token: the write is refreshed and resent once.
describe("an expired token's plain 401", () => {
  it("surfaces confirm's wrong code after a refresh, and keeps the session", async () => {
    const refreshed = token("acct-G");
    fetchMock
      .mockResolvedValueOnce(json({ title: "Unauthorized" }, 401))
      .mockResolvedValueOnce(pair(refreshed))
      .mockResolvedValueOnce(json({ title: "Auth.EnrolmentCodeIncorrect", attemptsLeft: 2 }, 400));

    await expect(confirmTwoFactor(freshCode()))
      .rejects.toMatchObject({ status: 400, title: "Auth.EnrolmentCodeIncorrect", extensions: { attemptsLeft: 2 } });
    expect(onUnauth).not.toHaveBeenCalled();
    expect(getAccessToken()).toBe(refreshed);
  });

  it("refreshes and resends a write after an expired token's plain 401", async () => {
    fetchMock
      .mockResolvedValueOnce(json({ title: "Unauthorized" }, 401))
      .mockResolvedValueOnce(pair(token("acct-F")))
      .mockResolvedValueOnce(json({ recoveryCodes: [] }));
    await regenerateRecoveryCodes(crypto.randomUUID());

    expect(to("/auth/two-factor/recovery-codes")).toHaveLength(2);
  });
});

// #1277 review T1, U1 — from the moment sign-out starts until the next sign-in,
// no refresh sends, so no request can get a new token.
describe("after sign-out starts", () => {
  let held: ((res?: Response | Error) => Promise<void>) | null = null;
  afterEach(async () => { await held?.(); held = null; });

  // Sign-out with its request held; a refresh would succeed, so only the
  // refusal keeps one from happening.
  async function signOutHeld() {
    let finish!: (res: Response | Error) => void;
    fetchMock.mockImplementationOnce(() => new Promise<Response>((resolve, reject) => {
      finish = (res) => (res instanceof Error ? reject(res) : resolve(res));
    }));
    const done = logout();
    await vi.waitFor(() => expect(to("/auth/logout")).toHaveLength(1));
    fetchMock.mockImplementation(async () => pair(token("acct-S")));
    held = async (res: Response | Error = new Response(null, { status: 204 })) => { finish(res); await done; };
    return held;
  }

  it("refuses from its first line, before any await", async () => {
    let finish!: () => void;
    fetchMock.mockImplementationOnce(() => new Promise<Response>((resolve) => { finish = () => resolve(new Response(null, { status: 204 })); }));

    const done = logout();
    held = async () => { finish(); await done; };

    expect(isSignedOut()).toBe(true);
  });

  it.each([
    ["the status read", "GET", () => getTwoFactorStatus()],
    ["confirm", "POST", () => confirmTwoFactor(freshCode())],
    ["start", "POST", () => startTwoFactor(crypto.randomUUID())],
    ["turn-off", "DELETE", () => turnOffTwoFactor(crypto.randomUUID())],
  ] as const)("%s sends nothing and refreshes nothing", async (_, method, call) => {
    await signOutHeld();

    await expect(call()).rejects.toMatchObject({ status: 401 });

    expect(to("/auth/refresh")).toHaveLength(0);
    expect(calls().filter(([url, init]) => url.includes("/auth/two-factor") && (init.method ?? "GET") === method)).toHaveLength(0);
    expect(onUnauth).not.toHaveBeenCalled();
  });

  it.each([
    ["a session restore", () => restoreSession().then((restored) => { if (restored) throw new Error("restored"); })],
    ["a download", () => apiGetBlob("/exports/test").then(() => { throw new Error("downloaded"); }, () => undefined)],
  ] as const)("%s refreshes nothing", async (_, call) => {
    await signOutHeld();

    await call();

    expect(calls().map(([url]) => url)).toEqual(["/api/v1/auth/logout"]);
  });

  it.each([
    ["answers", new Response(null, { status: 204 })],
    ["fails", new TypeError("offline")],
  ] as const)("stays refused after the sign-out request %s, until the next sign-in", async (_, answer) => {
    const quiet = vi.spyOn(console, "error").mockImplementation(() => undefined);
    try {
      const settle = await signOutHeld();
      await settle(answer);
      held = null;

      await expect(getTwoFactorStatus()).rejects.toMatchObject({ status: 401 });

      expect(to("/auth/refresh")).toHaveLength(0);
    } finally {
      quiet.mockRestore();
    }
  });

  it("stays refused while an earlier sign-out is still out, though a later one has settled", async () => {
    let first!: () => void;
    fetchMock
      .mockImplementationOnce(() => new Promise<Response>((resolve) => { first = () => resolve(new Response(null, { status: 204 })); }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const a = logout();
    held = async () => { first(); await a; };
    await logout();
    fetchMock.mockImplementation(async () => pair(token("acct-S")));

    await expect(startTwoFactor(crypto.randomUUID())).rejects.toMatchObject({ status: 401 });

    expect(to("/auth/refresh")).toHaveLength(0);
    expect(to("/auth/two-factor/start")).toHaveLength(0);
  });

  it("refuses a refresh that was queued behind the cross-tab lock before sign-out", async () => {
    let enter!: () => Promise<unknown>;
    const queued = new Promise<void>((ready) => {
      vi.stubGlobal("navigator", { locks: { request: (_name: string, run: () => Promise<unknown>) => {
        ready();
        return new Promise((resolve, reject) => { enter = () => run().then(resolve, reject); });
      } } });
    });
    fetchMock.mockImplementation(async (url: string) => (url.endsWith("/auth/logout") ? new Response(null, { status: 204 }) : pair(token("acct-Q"))));
    const refresh = refreshTokens().then(() => "adopted", () => "refused");
    await queued;
    vi.stubGlobal("navigator", {});
    await logout();

    await enter().catch(() => undefined);

    expect(await refresh).toBe("refused");
    expect(to("/auth/refresh")).toHaveLength(0);
  });

  describe("with a sign-in that is abandoned before it commits", () => {
    afterEach(() => { vi.useRealTimers(); });

    // The cross-tab lock is held elsewhere (a password change in another
    // tab), so the sign-in waits behind it until its deadline passes.
    function lockHeldElsewhere() {
      const turns: (() => Promise<unknown>)[] = [];
      vi.stubGlobal("navigator", { locks: { request: (_name: string, run: () => Promise<unknown>) =>
        new Promise((resolve, reject) => { turns.push(() => run().then(resolve, reject)); }) } });
      return turns;
    }

    // Runs each turn as it arrives, the ones queued behind earlier turns too.
    async function drain(turns: (() => Promise<unknown>)[]) {
      for (let i = 0; i < turns.length; i++) {
        await turns[i]!().catch(() => undefined);
        await vi.advanceTimersByTimeAsync(0);
      }
    }

    it("refuses a refresh queued behind it, and adopts no token", async () => {
      vi.useFakeTimers();
      fetchMock.mockImplementation(async (url: string) => (url.endsWith("/auth/logout") ? new Response(null, { status: 204 }) : pair(token("acct-A"))));
      await logout();
      const turns = lockHeldElsewhere();
      const signIn = login({ farmCode: "farm", email: "a@example.test", password: crypto.randomUUID() }).then(() => "signed in", () => "abandoned");
      await vi.advanceTimersByTimeAsync(0);
      const parked = refreshTokens().then(() => "adopted", () => "refused");

      await vi.advanceTimersByTimeAsync(REFRESH_TIMEOUT_MS);
      expect(await signIn).toBe("abandoned");
      await drain(turns);

      expect(await parked).toBe("refused");
      expect(isSignedOut()).toBe(true);
      expect(getAccessToken()).toBeNull();
      expect(calls().map(([url]) => url)).toEqual(["/api/v1/auth/logout"]);
    });

    it("refuses the refresh an older write's 401 would supply, and does not resend the write", async () => {
      vi.useFakeTimers();
      let answer!: (res: Response) => void;
      fetchMock.mockImplementation(async (url: string) => (url.endsWith("/auth/logout") ? new Response(null, { status: 204 })
        : url.endsWith("/auth/refresh") ? pair(token("acct-W"))
        : new Promise<Response>((resolve) => { answer = resolve; })));
      const write = startTwoFactor(crypto.randomUUID()).then(() => "resent", () => "refused");
      await vi.advanceTimersByTimeAsync(0);
      await logout();
      const turns = lockHeldElsewhere();
      const signIn = login({ farmCode: "farm", email: "a@example.test", password: crypto.randomUUID() }).catch(() => undefined);
      await vi.advanceTimersByTimeAsync(0);

      answer(json({ title: "Unauthorized" }, 401));
      await vi.advanceTimersByTimeAsync(REFRESH_TIMEOUT_MS);
      await signIn;
      await drain(turns);

      expect(await write).toBe("refused");
      expect(to("/auth/two-factor/start")).toHaveLength(1);
      expect(to("/auth/refresh")).toHaveLength(0);
      expect(to("/auth/login")).toHaveLength(0);
    });
  });

  it("ends at the next sign-in, whose requests authenticate", async () => {
    const settle = await signOutHeld();
    await settle();
    held = null;
    fetchMock.mockReset();
    fetchMock
      .mockResolvedValueOnce(pair(token("acct-L")))
      .mockResolvedValueOnce(json({ enabled: false, recoveryCodesLeft: 0 }));

    await login({ farmCode: "farm", email: "a@example.test", password: crypto.randomUUID() });

    expect(isSignedOut()).toBe(false);
    await expect(getTwoFactorStatus()).resolves.toEqual({ enabled: false, recoveryCodesLeft: 0 });
  });
});

// #1277 review U2 — a sign-out the server never answers still ends locally.
describe("a sign-out the server never answers", () => {
  afterEach(() => { vi.useRealTimers(); });

  it("stops waiting at the refresh timeout, and never aborts the revoke", async () => {
    vi.useFakeTimers();
    fetchMock.mockImplementation(() => new Promise<Response>(() => undefined));
    let ended = false;
    void logout().then(() => { ended = true; });

    await vi.advanceTimersByTimeAsync(REFRESH_TIMEOUT_MS - 1);
    expect(ended).toBe(false);
    await vi.advanceTimersByTimeAsync(1);

    expect(ended).toBe(true);
    // The server revokes under the request's own cancellation token, so an
    // aborted request could leave the session live.
    const [, init] = to("/auth/logout")[0]!;
    expect(init.signal?.aborted ?? false).toBe(false);
  });
});
