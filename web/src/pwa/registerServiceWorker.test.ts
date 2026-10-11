import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { CacheExpiration } from "workbox-expiration";
import {
  activateUpdate,
  registerServiceWorker,
  serviceWorkerSupported,
} from "./registerServiceWorker";

vi.mock("workbox-expiration", () => ({
  CacheExpiration: vi.fn(class {
    updateTimestamp = vi.fn(() => Promise.resolve());
    expireEntries = vi.fn(() => Promise.resolve());
  }),
}));

// The guard that matters most here is the secure-context one. Barn phones reach
// this app over plain http today, where `navigator.serviceWorker` simply does
// not exist — the #138 black screen came from exactly that class of assumption,
// so these tests assert the no-op path as hard as the happy path.

type Listener = (...args: unknown[]) => void;

/**
 * A minimal EventTarget stand-in that lets a test fire a named event.
 *
 * It honours `{ signal }` and `removeEventListener` for real — an already-
 * aborted signal adds nothing, and aborting later detaches. Without that the
 * listener-cleanup test would be asserting against the fake rather than against
 * the code under test.
 */
function emitter() {
  const listeners = new Map<string, Listener[]>();
  const remove = (type: string, fn: Listener) =>
    listeners.set(type, (listeners.get(type) ?? []).filter((l) => l !== fn));
  return {
    addEventListener: vi.fn(
      (type: string, fn: Listener, opts?: { signal?: AbortSignal; once?: boolean }) => {
        if (opts?.signal?.aborted) return;
        listeners.set(type, [...(listeners.get(type) ?? []), fn]);
        opts?.signal?.addEventListener("abort", () => remove(type, fn));
      },
    ),
    removeEventListener: vi.fn(remove),
    fire(type: string) {
      for (const fn of [...(listeners.get(type) ?? [])]) fn();
    },
    listenerCount: (type: string) => (listeners.get(type) ?? []).length,
  };
}

function fakeWorker(state = "installing") {
  return { ...emitter(), state, postMessage: vi.fn() };
}

function fakeRegistration(over: Record<string, unknown> = {}) {
  return {
    ...emitter(),
    installing: null,
    waiting: null,
    active: null,
    update: vi.fn(() => Promise.resolve()),
    ...over,
  };
}

/** Installs a controllable navigator.serviceWorker + isSecureContext. */
function stubEnv({
  secure = true,
  hasApi = true,
  registration = fakeRegistration(),
  controller = {} as unknown,
  registerImpl,
}: {
  secure?: boolean;
  hasApi?: boolean;
  registration?: ReturnType<typeof fakeRegistration>;
  controller?: unknown;
  registerImpl?: () => Promise<unknown>;
} = {}) {
  vi.stubGlobal("isSecureContext", secure);
  const container = {
    ...emitter(),
    controller,
    register: vi.fn(registerImpl ?? (() => Promise.resolve(registration))),
  };
  // Deleting the key models an insecure context, where the API is absent.
  const nav = hasApi ? { serviceWorker: container } : {};
  vi.stubGlobal("navigator", nav);
  return { container, registration };
}

let testTeardown: AbortController;
function registerWorker(
  onUpdate?: Parameters<typeof registerServiceWorker>[0],
  signal: AbortSignal = testTeardown.signal,
) {
  return registerServiceWorker(onUpdate, signal);
}

beforeEach(() => {
  testTeardown = new AbortController();
  vi.stubGlobal("location", { reload: vi.fn() });
});
afterEach(() => {
  testTeardown.abort();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("serviceWorkerSupported", () => {
  it("is false off a secure context, even when the API object exists", () => {
    stubEnv({ secure: false });
    expect(serviceWorkerSupported()).toBe(false);
  });

  it("is false when the API is absent (plain http)", () => {
    stubEnv({ hasApi: false });
    expect(serviceWorkerSupported()).toBe(false);
  });

  it("is true on a secure context with the API present", () => {
    stubEnv();
    expect(serviceWorkerSupported()).toBe(true);
  });
});

describe("registerServiceWorker", () => {
  it("no-ops off a secure context — never touches register, never throws", async () => {
    const { container } = stubEnv({ secure: false });
    await expect(registerWorker()).resolves.toBeNull();
    expect(container.register).not.toHaveBeenCalled();
  });

  it("no-ops when the serviceWorker API is missing", async () => {
    stubEnv({ hasApi: false });
    await expect(registerWorker()).resolves.toBeNull();
  });

  it("registers the generated worker at the root scope", async () => {
    const { container, registration } = stubEnv();
    await expect(registerWorker()).resolves.toBe(registration);
    expect(container.register).toHaveBeenCalledWith("/sw.js", { scope: "/" });
  });

  it("checks for a new worker every hour while the registration is active", async () => {
    vi.useFakeTimers();
    try {
      const registration = fakeRegistration();
      stubEnv({ registration });
      const teardown = new AbortController();

      await registerWorker(undefined, teardown.signal);
      expect(registration.update).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(60 * 60_000);
      expect(registration.update).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(60 * 60_000);
      expect(registration.update).toHaveBeenCalledTimes(2);

      teardown.abort();
      await vi.advanceTimersByTimeAsync(60 * 60_000);
      expect(registration.update).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("checks for a new worker when a hidden app becomes visible", async () => {
    const registration = fakeRegistration();
    stubEnv({ registration });
    const teardown = new AbortController();
    const visibility = vi.spyOn(document, "visibilityState", "get");

    await registerWorker(undefined, teardown.signal);

    visibility.mockReturnValue("hidden");
    document.dispatchEvent(new Event("visibilitychange"));
    expect(registration.update).not.toHaveBeenCalled();

    visibility.mockReturnValue("visible");
    document.dispatchEvent(new Event("visibilitychange"));
    expect(registration.update).toHaveBeenCalledTimes(1);

    teardown.abort();
    document.dispatchEvent(new Event("visibilitychange"));
    expect(registration.update).toHaveBeenCalledTimes(1);
  });

  it("keeps a failed background update check silent", async () => {
    const update = vi.fn(() => Promise.reject(new Error("offline")));
    const registration = fakeRegistration({ update });
    stubEnv({ registration });
    const teardown = new AbortController();
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");

    await registerWorker(undefined, teardown.signal);
    document.dispatchEvent(new Event("visibilitychange"));
    await Promise.resolve(); // let the rejected update settle through its catch

    expect(update).toHaveBeenCalledTimes(1);
    teardown.abort();
  });

  it("keeps a synchronously failed background update check silent", async () => {
    const update = vi.fn(() => {
      throw new Error("browser quirk");
    });
    const registration = fakeRegistration({ update });
    stubEnv({ registration });
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");

    await registerWorker();

    expect(() =>
      document.dispatchEvent(new Event("visibilitychange")),
    ).not.toThrow();
    expect(update).toHaveBeenCalledTimes(1);
  });

  it("resolves null (does not reject) when registration fails", async () => {
    // A CSP forbidding workers, a proxy rewriting /sw.js, private-mode storage:
    // none of these should break the page that is already running.
    stubEnv({ registerImpl: () => Promise.reject(new Error("blocked by CSP")) });
    await expect(registerWorker()).resolves.toBeNull();
  });

  it("reports an update that was already waiting from a previous visit", async () => {
    const waiting = fakeWorker("installed");
    stubEnv({ registration: fakeRegistration({ waiting }) });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);

    expect(onUpdate).toHaveBeenCalledTimes(1);
    expect(typeof onUpdate.mock.calls[0][0]).toBe("function"); // the activator
  });

  it("reports an update that installs while the app is running", async () => {
    const installing = fakeWorker("installing");
    const registration = fakeRegistration({ installing });
    stubEnv({ registration });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);
    expect(onUpdate).not.toHaveBeenCalled();

    installing.state = "installed";
    registration.fire("updatefound");
    installing.fire("statechange");

    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("reports an update that was ALREADY installing when register() resolved", async () => {
    // The browser fires `updatefound` during registration, before there is
    // anywhere to attach a listener. Watching only future events would miss
    // this, leaving a long-lived tab on a stale build with no prompt.
    const installing = fakeWorker("installing");
    const registration = fakeRegistration({ installing });
    stubEnv({ registration });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);
    expect(onUpdate).not.toHaveBeenCalled(); // still installing

    installing.state = "installed";
    installing.fire("statechange"); // no `updatefound` is ever fired

    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("reports an update that finished installing before we could listen", async () => {
    // Same race, one step later: already `installed` by the time we look.
    const installing = fakeWorker("installed");
    stubEnv({ registration: fakeRegistration({ installing }) });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);

    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("removes its listeners when the caller aborts", async () => {
    const registration = fakeRegistration({ installing: fakeWorker("installing") });
    stubEnv({ registration });
    const controller = new AbortController();

    await registerWorker(vi.fn(), controller.signal);
    expect(registration.listenerCount("updatefound")).toBe(1);

    // Aborting detaches them — otherwise StrictMode's dev-mode effect replay
    // would leave a dead listener behind on every remount.
    controller.abort();
    expect(registration.listenerCount("updatefound")).toBe(0);

    // And a run started with an already-aborted signal attaches nothing.
    await registerWorker(vi.fn(), controller.signal);
    expect(registration.listenerCount("updatefound")).toBe(0);
  });

  it("announces an update ONCE when updatefound repeats the installing worker", async () => {
    // `updatefound` fires for the very worker that was already in `installing`
    // when register() resolved, so both entry points see it. Watching it twice
    // would announce the same update twice.
    const installing = fakeWorker("installing");
    const registration = fakeRegistration({ installing });
    stubEnv({ registration });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);
    registration.fire("updatefound"); // same worker, second path
    installing.state = "installed";
    installing.fire("statechange");

    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("stays silent on FIRST install — there is no update to announce", async () => {
    const installing = fakeWorker("installed");
    const registration = fakeRegistration({ installing });
    // No controller => nothing was controlling the page => this is install #1.
    stubEnv({ registration, controller: null });
    const onUpdate = vi.fn();

    await registerWorker(onUpdate);
    registration.fire("updatefound");
    installing.fire("statechange");

    expect(onUpdate).not.toHaveBeenCalled();
  });
});

describe("activateUpdate", () => {
  it("waits for controllerchange before reloading, so the reload lands on the NEW worker", async () => {
    const waiting = fakeWorker("installed");
    const { container } = stubEnv();
    const registration = fakeRegistration({ waiting });

    const pending = activateUpdate(registration as unknown as ServiceWorkerRegistration);

    // The waiting worker is told to take over...
    expect(waiting.postMessage).toHaveBeenCalledWith({ type: "SKIP_WAITING" });
    // ...and nothing reloads until it actually controls the page.
    expect(globalThis.location.reload).not.toHaveBeenCalled();

    container.fire("controllerchange");
    await pending;

    expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
  });

  it("settles on the worker reaching 'activated' — an UNCONTROLLED page never gets controllerchange", async () => {
    // The first worker doesn't claim existing clients and clientsClaim is off,
    // so a page that isn't yet controlled would otherwise wait out the whole
    // timeout before reloading.
    const waiting = fakeWorker("installed");
    stubEnv();
    const registration = fakeRegistration({ waiting });

    const pending = activateUpdate(registration as unknown as ServiceWorkerRegistration);
    waiting.state = "activated";
    waiting.fire("statechange");
    await pending;

    expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
  });

  it("reloads anyway if the worker never takes control, instead of hanging forever", async () => {
    // A promise that never settles is a permanently disabled "Reloading…"
    // button the user cannot escape. Reloading on the old version is
    // recoverable — the prompt simply comes back.
    vi.useFakeTimers();
    try {
      const waiting = fakeWorker("installed");
      stubEnv();
      const registration = fakeRegistration({ waiting });

      const pending = activateUpdate(registration as unknown as ServiceWorkerRegistration);
      expect(globalThis.location.reload).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(5_000); // nothing ever responds
      await pending;

      expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("still reloads when nothing is waiting (double-tap, or already activated)", async () => {
    stubEnv();
    await activateUpdate(fakeRegistration() as unknown as ServiceWorkerRegistration);
    expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
  });
});

// #1149 — the page that installs the first worker stays uncontrolled, so the
// language-packs route never sees the pack it imported.
describe("language packs on an uncontrolled page", () => {
  function stubPacksEnv(controller: unknown) {
    const { container } = stubEnv({ controller });
    Object.assign(container, { ready: Promise.resolve() });
    vi.stubGlobal("location", { reload: vi.fn(), origin: "https://farm.example" });
    // Behaves like Cache Storage: `add` stores one URL or rejects, and `addAll`
    // stores nothing when any URL in the batch fails.
    const stored = new Set<string>();
    const failing = new Set<string>();
    const cache = {
      add: vi.fn((url: string) => {
        if (failing.has(url)) return Promise.reject(new TypeError("Failed to fetch"));
        stored.add(url);
        return Promise.resolve();
      }),
      addAll: vi.fn((urls: string[]) => {
        if (urls.some((url) => failing.has(url))) return Promise.reject(new TypeError("Failed to fetch"));
        for (const url of urls) stored.add(url);
        return Promise.resolve();
      }),
    };
    vi.stubGlobal("caches", { open: vi.fn(() => Promise.resolve(cache)) });
    let deliver: ((names: string[]) => void) | undefined;
    const observe = vi.fn();
    vi.stubGlobal("PerformanceObserver", class {
      constructor(callback: (list: { getEntries(): { name: string }[] }) => void) {
        deliver = (names) => callback({ getEntries: () => names.map((name) => ({ name })) });
      }
      observe = observe;
      disconnect() {}
    });
    return { cache, stored, failing, observe, deliver: (names: string[]) => deliver?.(names) };
  }

  it("copies a pack the page loaded into the language-packs cache once the worker is ready", async () => {
    const { cache, observe, deliver } = stubPacksEnv(null);
    await registerWorker();
    expect(observe).toHaveBeenCalledWith({ type: "resource", buffered: true });

    deliver([
      "https://farm.example/assets/es-abc123.js",
      "https://farm.example/assets/index-abc123.js",
      "https://cdn.example/assets/tl-abc123.js",
    ]);
    await vi.waitFor(() => expect(cache.add).toHaveBeenCalledTimes(1));
    expect(globalThis.caches.open).toHaveBeenCalledWith("language-packs");
    expect(cache.add).toHaveBeenCalledWith("https://farm.example/assets/es-abc123.js");

    // The copy joins the route's four-entry expiration, then expiry runs.
    const expiration = vi.mocked(CacheExpiration).mock.instances.at(-1)!;
    await vi.waitFor(() => expect(expiration.expireEntries).toHaveBeenCalledTimes(1));
    expect(CacheExpiration).toHaveBeenLastCalledWith("language-packs", { maxEntries: 4 });
    expect(expiration.updateTimestamp).toHaveBeenCalledWith("https://farm.example/assets/es-abc123.js");
  });

  // The browser reports the copy's own fetch as a resource entry too. This
  // fake does the same, so a copier that reacts to its own fetches loops here
  // as it did in Chromium (#1149 review round 2).
  it("copies each pack once, though its own fetch shows up as a new resource entry", async () => {
    const { cache, deliver } = stubPacksEnv(null);
    cache.add.mockImplementation((url: string) => {
      setTimeout(() => deliver([url]), 0);
      return Promise.resolve();
    });
    await registerWorker();

    deliver(["https://farm.example/assets/es-abc123.js"]);
    await new Promise((resolve) => setTimeout(resolve, 50));
    deliver(["https://farm.example/assets/es-abc123.js"]);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(cache.add).toHaveBeenCalledTimes(1);
  });

  // #1149 review round 3 — a 404'd tl request and the es pack that did load
  // can reach the observer in one batch.
  it("still copies the pack that loaded when another in the same batch fails", async () => {
    const { stored, failing, deliver } = stubPacksEnv(null);
    const es = "https://farm.example/assets/es-abc123.js";
    const tl = "https://farm.example/assets/tl-abc123.js";
    failing.add(tl);
    await registerWorker();

    deliver([tl, es]);
    const expiration = await vi.waitFor(() => {
      const instance = vi.mocked(CacheExpiration).mock.instances.at(-1);
      expect(instance?.expireEntries).toHaveBeenCalledTimes(1);
      return instance!;
    });
    expect([...stored]).toEqual([es]);
    expect(expiration.updateTimestamp).toHaveBeenCalledTimes(1);
    expect(expiration.updateTimestamp).toHaveBeenCalledWith(es);
  });

  it("leaves a controlled page to the worker's own route", async () => {
    const { observe } = stubPacksEnv({});
    await registerWorker();
    expect(observe).not.toHaveBeenCalled();
  });
});
