import { describe, it, expect, vi, afterEach } from "vitest";
import { makeToken } from "../test/jwt";

// #1398 — a session that begins or ends in this tab tells the browser's other
// tabs, and says nothing else. Each test loads the modules fresh, as a new
// tab does.
const fresh = async () => {
  vi.resetModules();
  return {
    session: await import("../api/session"),
    refresh: await import("../api/refresh"),
    state: await import("../api/sessionState"),
    tokens: await import("./tokenStore"),
    signal: await import("./sessionSignal"),
  };
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" } });
const pair = (extra: object = {}) =>
  json({ accessToken: makeToken({ sub: "u1", role: "Admin", account_id: "acct-1" }), accessTokenExpiry: "2099-01-01T00:00:00Z", ...extra });
// The server answers `path` with `answer`, a sign-out with 204, and every
// other request with a 401.
const serve = (path: string, answer: () => Response) =>
  vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith(path) ? answer()
    : url.endsWith("/auth/logout") ? new Response(null, { status: 204 }) : json({ title: "Unauthorized" }, 401))));

const channels: BroadcastChannel[] = [];
const opened = (name: string) => {
  const channel = new BroadcastChannel(name);
  channels.push(channel);
  return channel;
};

// Another tab: its own channel, recording what it hears. heardSoFar() posts a
// marker from a third channel and waits for it, so everything posted before
// has arrived too, then returns the rest.
function otherTab(name: string) {
  const heard: unknown[] = [];
  const markers = new Set<unknown>();
  const channel = opened(name);
  channel.onmessage = (e: MessageEvent) => heard.push(e.data);
  const heardSoFar = async () => {
    const marker = crypto.randomUUID();
    markers.add(marker);
    opened(name).postMessage(marker);
    await vi.waitFor(() => expect(heard).toContain(marker));
    return heard.filter((m) => !markers.has(m));
  };
  return { post: () => channel.postMessage(null), heardSoFar };
}

afterEach(() => {
  for (const channel of channels.splice(0)) channel.close();
  vi.unstubAllGlobals();
});

const signIn = (session: typeof import("../api/session")) =>
  session.login({ farmCode: "farm", email: "u@farm.test", password: crypto.randomUUID() });
const holdSession = (tokens: typeof import("./tokenStore")) => {
  tokens.setAccessToken(makeToken({ sub: "u1", role: "Admin", account_id: "acct-1" }));
  tokens.bindAccount("acct-1");
};

describe("the session signal", () => {
  it("tells another tab, with no data, when this tab signs in and when it signs out", async () => {
    serve("/auth/login", pair);
    const { session, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);

    await signIn(session);
    expect(await other.heardSoFar()).toEqual([null]);

    await session.logout();
    expect(await other.heardSoFar()).toEqual([null, null]);
  });

  it("tells another tab when the second-factor step of a sign-in succeeds", async () => {
    serve("/auth/login/second-factor", pair);
    const { session, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);

    await session.completeSecondFactor(crypto.randomUUID(), "123456");

    expect(await other.heardSoFar()).toEqual([null]);
  });

  it("tells another tab at sign-out before the server answers", async () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => undefined)));
    const { session, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    holdSession(tokens);

    void session.logout();

    expect(await other.heardSoFar()).toEqual([null]);
  });

  it("tells another tab once when the server ends this tab's session: a 401, then a refused refresh", async () => {
    serve("/auth/refresh", () => json({ title: "Auth.SessionChanged" }, 401));
    const { refresh, state, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    const ended = vi.fn();
    state.setOnUnauthenticated(ended);
    holdSession(tokens);

    await expect(refresh.apiGet("/flocks")).rejects.toThrow();

    expect(tokens.getAccessToken()).toBeNull();
    expect(ended).toHaveBeenCalled();
    expect(await other.heardSoFar()).toEqual([null]);
  });

  it("tells another tab when a fresh token is refused too: a 401 after the refresh", async () => {
    serve("/auth/refresh", pair);
    const { refresh, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    holdSession(tokens);

    await expect(refresh.apiGet("/flocks")).rejects.toThrow();

    expect(tokens.getAccessToken()).toBeNull();
    expect(await other.heardSoFar()).toEqual([null]);
  });

  it("tells another tab once when three requests are refused together", async () => {
    serve("/auth/refresh", () => json({ title: "Auth.SessionChanged" }, 401));
    const { refresh, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    holdSession(tokens);

    await Promise.allSettled([refresh.apiGet("/a"), refresh.apiGet("/b"), refresh.apiGet("/c")]);

    expect(tokens.getAccessToken()).toBeNull();
    expect(await other.heardSoFar()).toEqual([null]);
  });

  // Review r2, probes Q1 and Q2: the server ended no session, so the other
  // tabs keep their codes. This tab still ends its own (client.test.ts).
  it.each([
    ["answers 500", () => json({ title: "Server error" }, 500)],
    ["fails on the network", () => { throw new TypeError("Failed to fetch"); }],
  ])("sends nothing when the refresh after a 401 %s", async (_, answer) => {
    serve("/auth/refresh", answer);
    const { refresh, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    holdSession(tokens);

    await expect(refresh.apiGet("/flocks")).rejects.toThrow();

    expect(await other.heardSoFar()).toEqual([]);
  });

  it("sends nothing from a tab that held no session, when its first refresh is refused", async () => {
    serve("/auth/refresh", () => json({ title: "Auth.SessionChanged" }, 401));
    const { refresh, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);

    await expect(refresh.restoreSession()).resolves.toBe(false);

    expect(await other.heardSoFar()).toEqual([]);
  });

  it("sends nothing when a sign-in fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json({ title: "Auth.InvalidCredentials" }, 401)));
    const { session, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);

    await expect(signIn(session)).rejects.toThrow();

    expect(await other.heardSoFar()).toEqual([]);
  });

  it("sends nothing for a silent refresh", async () => {
    serve("/auth/refresh", pair);
    const { refresh, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);

    await refresh.refreshTokens();

    expect(tokens.getAccessToken()).not.toBeNull();
    expect(await other.heardSoFar()).toEqual([]);
  });

  it.each([
    ["confirming an enrolment", "/auth/two-factor/confirm", () => pair({ recoveryCodes: ["a"] }),
      (s: typeof import("../api/session")) => s.confirmTwoFactor("123456")],
    ["changing the password", "/auth/change-password", () => pair(),
      (s: typeof import("../api/session")) => s.changePassword({ currentPassword: crypto.randomUUID(), newPassword: crypto.randomUUID() })],
    ["turning two-factor off", "/auth/two-factor", () => pair(),
      (s: typeof import("../api/session")) => s.turnOffTwoFactor(crypto.randomUUID())],
  ])("sends nothing for a write that hands this tab new tokens: %s", async (_, path, answer, write) => {
    serve(path, answer);
    const { session, tokens, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    holdSession(tokens);

    await write(session);

    expect(tokens.getAccessToken()).not.toBeNull();
    expect(await other.heardSoFar()).toEqual([]);
  });

  it("calls a listener for every signal from another tab, and not after it unsubscribes", async () => {
    const { signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    const listener = vi.fn();

    const stop = signal.onSessionChangeElsewhere(listener);
    other.post();
    other.post();
    await vi.waitFor(() => expect(listener).toHaveBeenCalledTimes(2));

    stop();
    other.post();
    await other.heardSoFar();
    expect(listener).toHaveBeenCalledTimes(2);
  });

  it("never hears this tab's own signal", async () => {
    serve("/auth/login", pair);
    const { session, signal } = await fresh();
    const other = otherTab(signal.SESSION_CHANNEL);
    const listener = vi.fn();
    signal.onSessionChangeElsewhere(listener);

    await signIn(session);
    await session.logout();
    // The marker comes from another channel, so the listener hears that one.
    await other.heardSoFar();
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it("does nothing, and sign-in and sign-out still work, where BroadcastChannel is missing", async () => {
    serve("/auth/login", pair);
    vi.stubGlobal("BroadcastChannel", undefined);
    const { session, signal } = await fresh();
    const listener = vi.fn();

    const stop = signal.onSessionChangeElsewhere(listener);
    await expect(signIn(session)).resolves.toEqual({ kind: "signedIn" });
    await expect(session.logout()).resolves.toBeUndefined();
    stop();
    expect(listener).not.toHaveBeenCalled();
  });
});
