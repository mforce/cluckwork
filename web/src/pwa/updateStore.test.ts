import { describe, it, expect, vi, beforeEach } from "vitest";
import { registerServiceWorker } from "./registerServiceWorker";
import {
  activateWaitingUpdate, dismissUpdate, getUpdateState, reopenUpdate,
  resetUpdateStoreForTests, startUpdateWatch, subscribeToUpdateState,
} from "./updateStore";

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

beforeEach(() => {
  vi.resetAllMocks();
  resetUpdateStoreForTests();
});

// #936 — this store is the ONE place the app calls registerServiceWorker;
// UpdatePrompt and the More-menu recovery action both read/dispatch against
// it instead of each holding their own registration.
describe("updateStore (#936)", () => {
  it("registers exactly once even if startUpdateWatch is called from multiple mounted consumers", () => {
    mockRegister.mockResolvedValue(null);
    const a = new AbortController();
    const b = new AbortController();
    startUpdateWatch(a.signal);
    startUpdateWatch(b.signal);
    expect(mockRegister).toHaveBeenCalledTimes(1);
  });

  it("re-registers after the watching signal aborts (StrictMode-style remount)", () => {
    mockRegister.mockResolvedValue(null);
    const first = new AbortController();
    startUpdateWatch(first.signal);
    first.abort();
    const second = new AbortController();
    startUpdateWatch(second.signal);
    expect(mockRegister).toHaveBeenCalledTimes(2);
  });

  it("stores the activator once the registration announces an update, and notifies subscribers", () => {
    let announce: ((activate: () => Promise<void>) => void) | undefined;
    mockRegister.mockImplementation(async (onUpdate) => {
      announce = onUpdate;
      return null;
    });
    const listener = vi.fn();
    subscribeToUpdateState(listener);
    startUpdateWatch(new AbortController().signal);

    const activate = vi.fn();
    announce?.(activate);

    expect(getUpdateState()).toEqual({ activate, dismissed: false, busy: false });
    expect(listener).toHaveBeenCalled();
  });

  it("Later (dismissUpdate) and the menu recovery action (reopenUpdate) toggle the same dismissed flag", () => {
    dismissUpdate();
    expect(getUpdateState().dismissed).toBe(true);
    reopenUpdate();
    expect(getUpdateState().dismissed).toBe(false);
  });

  it("a newly announced update re-earns attention even after an earlier one was dismissed", () => {
    let announce: ((activate: () => Promise<void>) => void) | undefined;
    mockRegister.mockImplementation(async (onUpdate) => {
      announce = onUpdate;
      return null;
    });
    startUpdateWatch(new AbortController().signal);
    announce?.(vi.fn());
    dismissUpdate();
    expect(getUpdateState().dismissed).toBe(true);

    announce?.(vi.fn()); // a second deploy lands
    expect(getUpdateState().dismissed).toBe(false);
  });

  it("activateWaitingUpdate does nothing when no update is waiting", async () => {
    await activateWaitingUpdate();
    expect(getUpdateState()).toEqual({ activate: null, dismissed: false, busy: false });
  });

  it("activateWaitingUpdate guards against double activation", async () => {
    let announce: ((activate: () => Promise<void>) => void) | undefined;
    mockRegister.mockImplementation(async (onUpdate) => {
      announce = onUpdate;
      return null;
    });
    startUpdateWatch(new AbortController().signal);
    let release: () => void = () => {};
    const activate = vi.fn(() => new Promise<void>((r) => { release = r; }));
    announce?.(activate);

    const first = activateWaitingUpdate();
    const second = activateWaitingUpdate(); // fired while the first is still pending
    expect(getUpdateState().busy).toBe(true);

    release();
    await Promise.all([first, second]);
    expect(activate).toHaveBeenCalledTimes(1);
  });

  it("re-enables activation after a failed attempt", async () => {
    let announce: ((activate: () => Promise<void>) => void) | undefined;
    mockRegister.mockImplementation(async (onUpdate) => {
      announce = onUpdate;
      return null;
    });
    startUpdateWatch(new AbortController().signal);
    const activate = vi.fn().mockRejectedValue(new Error("worker vanished"));
    announce?.(activate);

    await activateWaitingUpdate();
    expect(getUpdateState().busy).toBe(false);
    expect(getUpdateState().activate).toBe(activate);
  });
});
