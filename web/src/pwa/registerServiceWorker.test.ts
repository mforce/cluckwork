import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  activateUpdate,
  registerServiceWorker,
  serviceWorkerSupported,
} from "./registerServiceWorker";

// The guard that matters most here is the secure-context one. Barn phones reach
// this app over plain http today, where `navigator.serviceWorker` simply does
// not exist — the #138 black screen came from exactly that class of assumption,
// so these tests assert the no-op path as hard as the happy path.

type Listener = (...args: unknown[]) => void;

/** A minimal EventTarget stand-in that lets a test fire a named event. */
function emitter() {
  const listeners = new Map<string, Listener[]>();
  return {
    addEventListener: vi.fn((type: string, fn: Listener) => {
      listeners.set(type, [...(listeners.get(type) ?? []), fn]);
    }),
    fire(type: string) {
      for (const fn of listeners.get(type) ?? []) fn();
    },
    listenerCount: (type: string) => (listeners.get(type) ?? []).length,
  };
}

function fakeWorker(state = "installing") {
  return { ...emitter(), state, postMessage: vi.fn() };
}

function fakeRegistration(over: Record<string, unknown> = {}) {
  return { ...emitter(), installing: null, waiting: null, active: null, ...over };
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

beforeEach(() => {
  vi.stubGlobal("location", { reload: vi.fn() });
});
afterEach(() => vi.unstubAllGlobals());

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
    await expect(registerServiceWorker()).resolves.toBeNull();
    expect(container.register).not.toHaveBeenCalled();
  });

  it("no-ops when the serviceWorker API is missing", async () => {
    stubEnv({ hasApi: false });
    await expect(registerServiceWorker()).resolves.toBeNull();
  });

  it("registers the generated worker at the root scope", async () => {
    const { container, registration } = stubEnv();
    await expect(registerServiceWorker()).resolves.toBe(registration);
    expect(container.register).toHaveBeenCalledWith("/sw.js", { scope: "/" });
  });

  it("resolves null (does not reject) when registration fails", async () => {
    // A CSP forbidding workers, a proxy rewriting /sw.js, private-mode storage:
    // none of these should break the page that is already running.
    stubEnv({ registerImpl: () => Promise.reject(new Error("blocked by CSP")) });
    await expect(registerServiceWorker()).resolves.toBeNull();
  });

  it("reports an update that was already waiting from a previous visit", async () => {
    const waiting = fakeWorker("installed");
    stubEnv({ registration: fakeRegistration({ waiting }) });
    const onUpdate = vi.fn();

    await registerServiceWorker(onUpdate);

    expect(onUpdate).toHaveBeenCalledTimes(1);
    expect(typeof onUpdate.mock.calls[0][0]).toBe("function"); // the activator
  });

  it("reports an update that installs while the app is running", async () => {
    const installing = fakeWorker("installing");
    const registration = fakeRegistration({ installing });
    stubEnv({ registration });
    const onUpdate = vi.fn();

    await registerServiceWorker(onUpdate);
    expect(onUpdate).not.toHaveBeenCalled();

    installing.state = "installed";
    registration.fire("updatefound");
    installing.fire("statechange");

    expect(onUpdate).toHaveBeenCalledTimes(1);
  });

  it("stays silent on FIRST install — there is no update to announce", async () => {
    const installing = fakeWorker("installed");
    const registration = fakeRegistration({ installing });
    // No controller => nothing was controlling the page => this is install #1.
    stubEnv({ registration, controller: null });
    const onUpdate = vi.fn();

    await registerServiceWorker(onUpdate);
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

  it("still reloads when nothing is waiting (double-tap, or already activated)", async () => {
    stubEnv();
    await activateUpdate(fakeRegistration() as unknown as ServiceWorkerRegistration);
    expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
  });
});
