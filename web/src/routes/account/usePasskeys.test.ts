import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { FormEvent } from "react";
import { usePasskeys } from "./usePasskeys";
import { clearAccessToken, setAccessToken } from "../../auth/tokenStore";
import { startPasskeyRegistration } from "../../api/cluckwork";

// #1284 (r1, Codex) — a create step the user has left starts no browser
// ceremony and sends no credential: the page's session is checked after the
// options arrive and again after the browser answers, before the grant is spent.
const auth = vi.hoisted(() => ({ isAuthenticated: true, twoFactor: false, credentialKey: "user:1" }));
vi.mock("../../auth/useAuth", () => ({ useAuth: () => auth }));
vi.mock("../../api/cluckwork", async (original) => ({
  ...await original<typeof import("../../api/cluckwork")>(),
  listPasskeys: vi.fn(async () => ({ enabled: true, passkeys: [] })),
  startPasskeyRegistration: vi.fn(),
}));
vi.mock("../../api/client", async (original) => ({
  ...await original<typeof import("../../api/client")>(),
  stepUp: vi.fn(async () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" })),
}));

const event = { preventDefault() {} } as FormEvent;
const create = vi.fn();
const fetchMock = vi.fn();
const options = () => ({ ceremony: crypto.randomUUID(), options: {} as PublicKeyCredentialCreationOptionsJSON });
const credential = () => ({ toJSON: () => ({ id: crypto.randomUUID(), type: "public-key" }) });
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => {
  auth.isAuthenticated = true;
  vi.clearAllMocks();
  setAccessToken(crypto.randomUUID());
  vi.stubGlobal("PublicKeyCredential", { parseCreationOptionsFromJSON: (v: unknown) => v });
  vi.stubGlobal("fetch", fetchMock);
  Object.defineProperty(navigator, "credentials", { configurable: true, value: { create } });
  fetchMock.mockImplementation(async () => new Response(JSON.stringify({ passkey: { id: "id", name: "Passkey" } }),
    { status: 201, headers: { "Content-Type": "application/json" } }));
});
afterEach(() => {
  vi.unstubAllGlobals();
  clearAccessToken();
});

async function toCreateStep() {
  const view = renderHook(() => usePasskeys());
  await waitFor(() => expect(view.result.current.list.data).not.toBeNull());
  act(() => view.result.current.openAdd());
  await act(async () => { await view.result.current.onStepUp(event); });
  expect(view.result.current.flow.step).toBe("create");
  return view;
}

it("opens no browser ceremony when the page unmounts while the options are pending", async () => {
  const waiting = deferred<ReturnType<typeof options>>();
  vi.mocked(startPasskeyRegistration).mockReturnValueOnce(waiting.promise);
  const view = await toCreateStep();
  let flight!: Promise<void>;
  act(() => { flight = view.result.current.onCreate(event); });
  view.unmount();
  await act(async () => { waiting.resolve(options()); await flight; });

  expect(create).not.toHaveBeenCalled();
});

it("sends no credential when the page unmounts while the browser is pending", async () => {
  vi.mocked(startPasskeyRegistration).mockResolvedValueOnce(options());
  const waiting = deferred<ReturnType<typeof credential>>();
  create.mockReturnValueOnce(waiting.promise);
  const view = await toCreateStep();
  let flight!: Promise<void>;
  act(() => { flight = view.result.current.onCreate(event); });
  await waitFor(() => expect(create).toHaveBeenCalledOnce());
  view.unmount();
  await act(async () => { waiting.resolve(credential()); await flight; });

  expect(fetchMock).not.toHaveBeenCalled();
});

it("opens no browser ceremony after a logout while the options are pending", async () => {
  const waiting = deferred<ReturnType<typeof options>>();
  vi.mocked(startPasskeyRegistration).mockReturnValueOnce(waiting.promise);
  const view = await toCreateStep();
  let flight!: Promise<void>;
  act(() => { flight = view.result.current.onCreate(event); });
  await act(async () => {
    auth.isAuthenticated = false;
    view.rerender();
  });
  expect(view.result.current.flow.step).toBe("list");
  await act(async () => { waiting.resolve(options()); await flight; });

  expect(create).not.toHaveBeenCalled();
});
