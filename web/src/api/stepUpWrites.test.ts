import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { confirmTwoFactor, setOnTokensChanged, setOnUnauthenticated, STEP_UP_HEADER, turnOffTwoFactor } from "./client";
import {
  assignFlock, changeUserEmail, changeUserRole, createUser, disableUser, enableUser, regenerateRecoveryCodes,
  registerPasskey, removePasskey, removeUserPasskeys, resetUserTwoFactor, setUserPassword, startTwoFactor, unassignFlock,
} from "./cluckwork";
import { approveConsent } from "./oauth";
import { clearAccessToken, clearBoundAccount, getAccessToken, setAccessToken } from "../auth/tokenStore";
import i18n from "../i18n";
import { freshCode } from "../test/codes";
import { freshPassword } from "../test/passwords";

// #1284 (r1 Opus, r2 Opus and Fable) — every request that carries a step-up
// grant spends it, so a 401 Auth.CredentialsSuperseded on it goes back to the
// caller unsent again, with the session kept; so does a two-factor write,
// which has no grant. An ordinary expired-token 401 still refreshes and resends.
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const superseded = () => json({ title: "Auth.CredentialsSuperseded", detail: "Your credentials changed." }, 401);
const b64url = (s: string) => btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const token = () => `hdr.${b64url(JSON.stringify({ sub: "u1", account_id: "acct-A", exp: 9999999999 }))}.sig`;
const grant = () => crypto.randomUUID();
const credential = () => ({ id: crypto.randomUUID(), rawId: crypto.randomUUID(), type: "public-key" }) as unknown as RegistrationResponseJSON;

// The grant-carrying requests: the Users writes, consent (a GET), the
// two-factor writes and the passkey writes.
const WITH_GRANT: [string, () => Promise<unknown>][] = [
  ["createUser", () => createUser({ email: "a@farm.test", password: freshPassword(), role: "Worker" }, "k", grant())],
  ["setUserPassword", () => setUserPassword("u2", { newPassword: freshPassword() }, "k", grant())],
  ["changeUserRole", () => changeUserRole("u2", { role: "Admin" }, "k", grant())],
  ["changeUserEmail", () => changeUserEmail("u2", { email: "n@farm.test" }, "k", grant())],
  ["disableUser", () => disableUser("u2", { reason: null }, "k", grant())],
  ["enableUser", () => enableUser("u2", "k", grant())],
  ["assignFlock", () => assignFlock("u2", "f1", "k", grant())],
  ["unassignFlock", () => unassignFlock("u2", "a1", "k", grant())],
  ["resetUserTwoFactor", () => resetUserTwoFactor("u2", "k", grant())],
  ["removeUserPasskeys", () => removeUserPasskeys("u2", "k", grant())],
  ["approveConsent (a GET)", () => approveConsent("?client_id=x", grant(), ["read"])],
  ["startTwoFactor", () => startTwoFactor(grant())],
  ["regenerateRecoveryCodes", () => regenerateRecoveryCodes(grant())],
  ["turnOffTwoFactor", () => turnOffTwoFactor(grant())],
  ["registerPasskey", () => registerPasskey({ ceremony: crypto.randomUUID(), credential: credential(), name: "Passkey" }, grant())],
  ["removePasskey", () => removePasskey("id", grant())],
];

let fetchMock: ReturnType<typeof vi.fn>;
let onUnauth: ReturnType<typeof vi.fn<() => void>>;
beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  setOnTokensChanged(vi.fn());
  onUnauth = vi.fn();
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

describe("a 401 Auth.CredentialsSuperseded", () => {
  it.each([...WITH_GRANT, ["confirmTwoFactor (no grant)", () => confirmTwoFactor(freshCode())]])(
    "on %s goes back to the caller, sent once, with the session kept", async (_, send) => {
      fetchMock.mockResolvedValueOnce(superseded());

      await expect(send()).rejects.toMatchObject({
        status: 401, title: "Auth.CredentialsSuperseded", message: i18n.t("errors:Auth.CredentialsSuperseded"),
      });
      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(onUnauth).not.toHaveBeenCalled();
      expect(getAccessToken()).toBe("at1");
    });
});

describe("an expired token's plain 401", () => {
  it.each(WITH_GRANT.slice(0, 11))("on %s still refreshes and resends with the same grant", async (_, send) => {
    fetchMock
      .mockResolvedValueOnce(json({ title: "Unauthorized" }, 401))
      .mockResolvedValueOnce(json({ accessToken: token(), accessTokenExpiry: "2099-01-01T00:00:00Z" }))
      .mockResolvedValueOnce(json({ id: "x", redirectUri: "https://app.example.test/cb" }));

    await send();

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls).toHaveLength(3);
    const sent = new Headers(calls[0]![1].headers).get(STEP_UP_HEADER);
    expect(sent).toBeTruthy();
    expect(new Headers(calls[2]![1].headers).get(STEP_UP_HEADER)).toBe(sent);
  });
});
