import { describe, it, expect, vi, afterEach } from "vitest";
import { makeToken } from "../test/jwt";

// #1398 — a sign-in or sign-out in this tab tells the browser's other tabs,
// and says nothing else. Each test loads the modules fresh, as a new tab does.
const fresh = async () => {
  vi.resetModules();
  return {
    session: await import("../api/session"),
    signal: await import("./sessionSignal"),
  };
};

function serveLogin() {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith("/auth/login")
    ? new Response(JSON.stringify({ accessToken: makeToken({ sub: "u1", role: "Worker", account_id: "acct-1" }), accessTokenExpiry: "2099-01-01T00:00:00Z" }), { status: 200, headers: { "Content-Type": "application/json" } })
    : new Response(null, { status: 204 }))));
}

// Another tab: its own channel, recording what it hears.
function otherTab(name: string) {
  const heard: unknown[] = [];
  const channel = new BroadcastChannel(name);
  channel.onmessage = (e: MessageEvent) => heard.push(e.data);
  return { heard, post: () => channel.postMessage(null), close: () => channel.close() };
}

const tabs: { close: () => void }[] = [];
const open = (name: string) => {
  const tab = otherTab(name);
  tabs.push(tab);
  return tab;
};

afterEach(() => {
  for (const tab of tabs.splice(0)) tab.close();
  vi.unstubAllGlobals();
});

const signIn = (session: typeof import("../api/session")) =>
  session.login({ farmCode: "farm", email: "u@farm.test", password: crypto.randomUUID() });

describe("the session signal", () => {
  it("tells another tab, with no data, when this tab signs in and when it signs out", async () => {
    serveLogin();
    const { session, signal } = await fresh();
    const other = open(signal.SESSION_CHANNEL);

    await signIn(session);
    await vi.waitFor(() => expect(other.heard).toEqual([null]));

    await session.logout();
    await vi.waitFor(() => expect(other.heard).toEqual([null, null]));
  });

  it("sends nothing when a sign-in fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ title: "Auth.InvalidCredentials" }), { status: 401, headers: { "Content-Type": "application/problem+json" } })));
    const { session, signal } = await fresh();
    const other = open(signal.SESSION_CHANNEL);

    await expect(signIn(session)).rejects.toThrow();
    await new Promise((r) => setTimeout(r, 20));
    expect(other.heard).toEqual([]);
  });

  it("calls a listener when another tab signals, and not after it unsubscribes", async () => {
    const { signal } = await fresh();
    const other = open(signal.SESSION_CHANNEL);
    const listener = vi.fn();

    const stop = signal.onSessionChangeElsewhere(listener);
    other.post();
    await vi.waitFor(() => expect(listener).toHaveBeenCalledTimes(1));

    stop();
    other.post();
    await new Promise((r) => setTimeout(r, 20));
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it("never hears this tab's own signal", async () => {
    serveLogin();
    const { session, signal } = await fresh();
    const listener = vi.fn();
    signal.onSessionChangeElsewhere(listener);

    await signIn(session);
    await session.logout();
    await new Promise((r) => setTimeout(r, 20));
    expect(listener).not.toHaveBeenCalled();
  });

  it("does nothing, and sign-in and sign-out still work, where BroadcastChannel is missing", async () => {
    serveLogin();
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
