import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { changePassword, confirmTwoFactor, logout, setOnTokensChanged, setOnUnauthenticated, STEP_UP_HEADER, turnOffTwoFactor } from "./client";
import { getTwoFactorStatus, regenerateRecoveryCodes, startTwoFactor } from "./cluckwork";
import { clearAccessToken, clearBoundAccount, getAccessToken, getBoundAccountId, setAccessToken } from "../auth/tokenStore";
import { freshCode } from "../test/codes";

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

    await expect(turnOff).rejects.toThrow();
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

    await expect(write()).rejects.toThrow();
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
