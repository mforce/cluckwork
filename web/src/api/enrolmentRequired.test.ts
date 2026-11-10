import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiGet, apiGetBlob, logout, setOnTwoFactorEnrolmentRequired, setOnUnauthenticated } from "./client";
import { ApiError } from "./errors";
import { clearAccessToken, clearBoundAccount, setAccessToken } from "../auth/tokenStore";

// #1281 — the Owner gate's 403 Auth.TwoFactorEnrolmentRequired, on any send of
// an authenticated request in the current session, calls the callback that
// leads to the setup screen, and still rejects to the caller.
const GATE = "Auth.TwoFactorEnrolmentRequired";
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const b64url = (s: string) => btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const token = () => `hdr.${b64url(JSON.stringify({ sub: crypto.randomUUID(), account_id: "acct-A", exp: 9999999999 }))}.sig`;
const pair = () => json({ accessToken: token(), accessTokenExpiry: "2099-01-01T00:00:00Z" });
const problem = (status: number, title: string) => json({ title, detail: crypto.randomUUID() }, status);

let fetchMock: ReturnType<typeof vi.fn>;
let onRequired: ReturnType<typeof vi.fn<() => void>>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  onRequired = vi.fn();
  setOnTwoFactorEnrolmentRequired(onRequired);
  setOnUnauthenticated(vi.fn());
  setAccessToken("at1");
  clearBoundAccount();
});

afterEach(() => {
  vi.unstubAllGlobals();
  setOnTwoFactorEnrolmentRequired(null);
  setOnUnauthenticated(null);
  clearAccessToken();
  clearBoundAccount();
});

describe("the Owner gate's 403", () => {
  it("calls the callback and still rejects with the same ApiError", async () => {
    const detail = crypto.randomUUID();
    const traceRef = crypto.randomUUID();
    fetchMock.mockResolvedValueOnce(json({ title: GATE, detail, traceRef }, 403));

    const err = await apiGet("/flocks").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 403, title: GATE, message: detail, extensions: { traceRef } });
    expect(onRequired).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["another 403 title", 403, "Auth.OwnerCannotTurnOffTwoFactor"],
    ["a plain 403", 403, "Auth.Forbidden"],
    ["the gate's title on a 400", 400, GATE],
  ])("does nothing on %s", async (_, status, title) => {
    fetchMock.mockResolvedValueOnce(problem(status, title));

    await expect(apiGet("/flocks")).rejects.toMatchObject({ status, title });
    expect(onRequired).not.toHaveBeenCalled();
  });

  it("calls the callback when the resend after a refresh gets it", async () => {
    fetchMock
      .mockResolvedValueOnce(problem(401, "Unauthorized"))
      .mockResolvedValueOnce(pair())
      .mockResolvedValueOnce(problem(403, GATE));

    await expect(apiGet("/flocks")).rejects.toMatchObject({ status: 403, title: GATE });
    expect(onRequired).toHaveBeenCalledTimes(1);
  });

  it("calls the callback for a download", async () => {
    fetchMock.mockResolvedValueOnce(problem(403, GATE));

    await expect(apiGetBlob("/exports/test")).rejects.toMatchObject({ status: 403, title: GATE });
    expect(onRequired).toHaveBeenCalledTimes(1);
  });

  // A request sent before a sign-out must not put the next user on the setup
  // screen when its answer lands after.
  it("does nothing when it answers a request from a session that has since ended", async () => {
    let answer!: (res: Response) => void;
    fetchMock.mockImplementation((url: string) => (String(url).endsWith("/flocks")
      ? new Promise<Response>((resolve) => { answer = resolve; })
      : Promise.resolve(new Response(null, { status: 204 }))));
    const pending = apiGet("/flocks").catch((e: unknown) => e);
    await vi.waitFor(() => expect(answer).toBeDefined());

    await logout();
    answer(problem(403, GATE));

    expect(await pending).toMatchObject({ status: 403, title: GATE });
    expect(onRequired).not.toHaveBeenCalled();
  });
});
