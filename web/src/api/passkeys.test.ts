import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { setOnTokensChanged, STEP_UP_HEADER } from "./client";
import { registerPasskey, removePasskey, removeUserPasskeys } from "./cluckwork";
import { clearAccessToken, clearBoundAccount, getAccessToken, setAccessToken } from "../auth/tokenStore";
import { apiGet, setOnUnauthenticated } from "./client";
import i18n from "../i18n";

// #1284 — registering and removing your own passkey (record 1282 decisions
// 10 and 16). Removal always hands this device a fresh pair; registration only
// when the passkey was the first factor.
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const b64url = (s: string) => btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const token = () =>
  `hdr.${b64url(JSON.stringify({ sub: crypto.randomUUID(), account_id: "acct-A", exp: 9999999999 }))}.sig`;
const summary = () => ({ id: crypto.randomUUID(), name: "Phone", createdAt: "2026-11-24T00:00:00Z", synced: false, address: null });
const body = () => ({
  ceremony: crypto.randomUUID(),
  credential: { id: crypto.randomUUID(), rawId: crypto.randomUUID(), type: "public-key" } as unknown as RegistrationResponseJSON,
  name: "Phone",
});

type Call = [string, RequestInit];
let fetchMock: ReturnType<typeof vi.fn>;
let onTokens: ReturnType<typeof vi.fn<() => void>>;
const call = () => fetchMock.mock.calls[0] as Call;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  onTokens = vi.fn();
  setOnTokensChanged(onTokens);
  setAccessToken("at1");
  clearBoundAccount();
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnTokensChanged(null);
  clearAccessToken();
  clearBoundAccount();
});

describe("registerPasskey", () => {
  it("sends the ceremony, credential and name with the grant, and keeps the session on a 201", async () => {
    const passkey = summary();
    fetchMock.mockResolvedValueOnce(json({ passkey }, 201));
    const sent = body();
    const grant = crypto.randomUUID();

    await expect(registerPasskey(sent, grant)).resolves.toEqual(passkey);

    expect(call()[0]).toMatch(/\/auth\/passkeys$/);
    expect(call()[1].method).toBe("POST");
    expect(new Headers(call()[1].headers).get(STEP_UP_HEADER)).toBe(grant);
    expect(JSON.parse(String(call()[1].body))).toEqual(sent);
    expect(getAccessToken()).toBe("at1");
    expect(onTokens).not.toHaveBeenCalled();
  });

  it("refuses an answer that holds no passkey", async () => {
    fetchMock.mockResolvedValueOnce(json({}, 201));

    await expect(registerPasskey(body(), crypto.randomUUID())).rejects.toThrow();
  });

  it("adopts the fresh pair a first factor answers with", async () => {
    const fresh = token();
    fetchMock.mockResolvedValueOnce(json({ passkey: summary(), accessToken: fresh, accessTokenExpiry: "2099-01-01T00:00:00Z" }));

    await registerPasskey(body(), crypto.randomUUID());

    expect(getAccessToken()).toBe(fresh);
    expect(onTokens).toHaveBeenCalled();
  });
});

describe("removePasskey", () => {
  it("deletes the passkey by its id with the grant and adopts the fresh pair", async () => {
    const fresh = token();
    fetchMock.mockResolvedValueOnce(json({ accessToken: fresh, accessTokenExpiry: "2099-01-01T00:00:00Z" }));
    const grant = crypto.randomUUID();

    await removePasskey("a-_b", grant);

    expect(call()[0]).toMatch(/\/auth\/passkeys\/a-_b$/);
    expect(call()[1].method).toBe("DELETE");
    expect(new Headers(call()[1].headers).get(STEP_UP_HEADER)).toBe(grant);
    expect(getAccessToken()).toBe(fresh);
  });

  it("treats an answer without a fresh pair as broken", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    await expect(removePasskey("id", crypto.randomUUID())).rejects.toThrow();
    expect(getAccessToken()).toBe("at1");
  });
});

describe("removeUserPasskeys", () => {
  it("sends a DELETE to the user's passkeys route with the key and the step-up grant", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    const grant = crypto.randomUUID();

    await removeUserPasskeys("u-e", "key-1", grant);

    expect(call()[0]).toMatch(/\/users\/u-e\/passkeys$/);
    expect(call()[1].method).toBe("DELETE");
    expect(new Headers(call()[1].headers).get("Idempotency-Key")).toBe("key-1");
    expect(new Headers(call()[1].headers).get(STEP_UP_HEADER)).toBe(grant);
  });
});

// r2 (Fable G3) — the same title also ends a session whose refresh is refused,
// so the one catalog text must hold there too.
it("words a read whose session really ended with the same catalog text", async () => {
  const onUnauth = vi.fn();
  setOnUnauthenticated(onUnauth);
  fetchMock
    .mockResolvedValueOnce(json({ title: "Auth.CredentialsSuperseded", detail: "Your credentials changed." }, 401))
    .mockResolvedValueOnce(json({ title: "Identity.InvalidRefreshToken", detail: "The refresh token is invalid." }, 401));

  await expect(apiGet("/stock")).rejects.toMatchObject({ message: i18n.t("errors:Auth.CredentialsSuperseded") });
  expect(onUnauth).toHaveBeenCalledWith("Auth.CredentialsSuperseded");
  setOnUnauthenticated(null);
});
