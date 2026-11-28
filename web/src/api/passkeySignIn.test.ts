import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  login,
  completeSecondFactor,
  passkeyLogin,
  startPasskeySignIn,
  startSecondFactorPasskey,
  ApiError,
} from "./client";
import { getAccessToken, setAccessToken, clearAccessToken, clearBoundAccount, getBoundAccountId } from "../auth/tokenStore";

// #1285 — the passkey half of the sign-in API (record 1282): the pending 401's
// secondFactors, passkey sign-in, the two options routes and the passkey
// answer at the second step. The harness is client.test.ts's, cut to what
// these cases use.

const FUTURE = "2099-01-01T00:00:00Z";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const b64url = (input: string) => btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const jwtWithAccountId = (accountId: string, marker: string) =>
  `hdr.${b64url(JSON.stringify({ sub: `user-${marker}`, account_id: accountId, exp: 9999999999 }))}.sig`;

type Call = [string, RequestInit];
const callsTo = (mock: ReturnType<typeof vi.fn>, suffix: string) =>
  (mock.mock.calls as Call[]).filter(([url]) => url.endsWith(suffix));

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  clearAccessToken();
  clearBoundAccount();
});

afterEach(() => {
  vi.unstubAllGlobals();
  clearAccessToken();
  clearBoundAccount();
});

describe("passkey sign-in API (#1285)", () => {
  // #1285 — the pending 401's secondFactors names the proofs; unknown names are
  // dropped, and a list naming none is not a usable second step.
  it.each([
    [["passkey"], ["passkey"]],
    [["passkey", "authenticator", "sms"], ["authenticator", "passkey"]],
  ])("login reads secondFactors %j as %j", async (sent, factors) => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      title: "Auth.SecondFactorRequired", status: 401, challenge: "challenge-jwt", challengeExpiresAt: FUTURE, secondFactors: sent,
    }, 401));

    await expect(login({ farmCode: "default-farm", email: "a@b.co", password: "pw" }))
      .resolves.toMatchObject({ kind: "secondFactor", factors });
  });

  it.each([[undefined], [[]], [["sms"]], ["passkey"]])("login rethrows a pending 401 whose secondFactors is %j", async (sent) => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      title: "Auth.SecondFactorRequired", status: 401, challenge: "challenge-jwt", challengeExpiresAt: FUTURE, secondFactors: sent,
    }, 401));

    await expect(login({ farmCode: "default-farm", email: "a@b.co", password: "pw" })).rejects.toBeInstanceOf(ApiError);
  });

  // #1285 — a passkey sign-in is one anonymous request: a blank farm code goes
  // as null, a refusal is never refreshed and resent, and success signs in.
  it("passkeyLogin sends the ceremony, the credential and the farm code, and signs in like login", async () => {
    clearAccessToken();
    clearBoundAccount();
    const answer = { ceremony: "c-1", credential: { id: "cred" } as unknown as AuthenticationResponseJSON };
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "Auth.PasskeyRefused", status: 401 }, 401));

    await expect(passkeyLogin("  ", answer)).rejects.toMatchObject({ status: 401, title: "Auth.PasskeyRefused" });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(JSON.parse(String(callsTo(fetchMock, "/auth/login/passkey")[0][1].body)))
      .toEqual({ ceremony: "c-1", credential: { id: "cred" }, farmCode: null });

    fetchMock.mockResolvedValueOnce(jsonResponse({
      accessToken: jwtWithAccountId("acct-A", "passkey"), accessTokenExpiry: FUTURE, farmCode: "farm-a",
    }));
    await expect(passkeyLogin("Farm-A", answer)).resolves.toBe("farm-a");
    expect(JSON.parse(String(callsTo(fetchMock, "/auth/login/passkey")[1][1].body)).farmCode).toBe("Farm-A");
    expect(getAccessToken()).toBe(jwtWithAccountId("acct-A", "passkey"));
    expect(getBoundAccountId()).toBe("acct-A");
  });

  // #1285 — both passkey options routes are anonymous: a 401 is their answer,
  // never a reason to refresh and send again.
  it.each([
    ["the sign-in options", () => startPasskeySignIn(), "/auth/login/passkey/options"],
    ["the second step's options", () => startSecondFactorPasskey("challenge-jwt"), "/auth/login/second-factor/passkey-options"],
  ])("%s answer a 401 with one request and no refresh", async (_, call, path) => {
    setAccessToken(jwtWithAccountId("acct-at1", "at1"));
    fetchMock.mockResolvedValue(jsonResponse({ title: "Auth.SecondFactorChallengeExpired", status: 401 }, 401));

    await expect(call()).rejects.toMatchObject({ status: 401 });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(callsTo(fetchMock, path)).toHaveLength(1);
    expect(callsTo(fetchMock, "/auth/refresh")).toHaveLength(0);
  });

  it("completeSecondFactor sends a passkey answer as {challenge, passkey}", async () => {
    const answer = { ceremony: "c-2", credential: { id: "cred" } as unknown as AuthenticationResponseJSON };
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: "Auth.PasskeyRefused", status: 400 }, 400));

    await expect(completeSecondFactor("challenge-jwt", answer)).rejects.toMatchObject({ status: 400 });
    expect(JSON.parse(String(callsTo(fetchMock, "/auth/login/second-factor")[0][1].body)))
      .toEqual({ challenge: "challenge-jwt", passkey: { ceremony: "c-2", credential: { id: "cred" } } });
  });

});
