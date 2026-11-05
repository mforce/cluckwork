import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { FormEvent } from "react";
import { useTwoFactor } from "./useTwoFactor";
import { getTwoFactorStatus, regenerateRecoveryCodes } from "../../api/cluckwork";
import { stepUp } from "../../api/client";

// #1277 review W3 — the recovery codes leave the hook's state on every exit
// from the codes step, not only the rendered page. Adapted from the round-1
// runner's probe, which caught a summary that kept them.
const auth = vi.hoisted(() => ({ isAuthenticated: true }));
vi.mock("../../auth/useAuth", () => ({ useAuth: () => auth }));
vi.mock("../../api/cluckwork", async (original) => ({
  ...await original<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(), regenerateRecoveryCodes: vi.fn(),
}));
vi.mock("../../api/client", async (original) => ({
  ...await original<typeof import("../../api/client")>(), stepUp: vi.fn(),
}));

beforeEach(() => {
  auth.isAuthenticated = true;
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
  vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: [crypto.randomUUID()] });
  vi.mocked(stepUp).mockResolvedValue({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
});

async function onCodesStep() {
  const hook = renderHook(useTwoFactor);
  await waitFor(() => expect(hook.result.current.status.data?.enabled).toBe(true));
  act(() => hook.result.current.openProof("regenerate"));
  act(() => hook.result.current.setProof({ password: crypto.randomUUID() }));
  await act(async () => { await hook.result.current.onProof({ preventDefault() {} } as FormEvent); });
  expect(hook.result.current.flow.step).toBe("codes");
  return hook;
}

describe("the recovery codes' lifetime", () => {
  it("ends when the user leaves the codes step", async () => {
    const { result } = await onCodesStep();

    act(() => result.current.leaveFlow());

    expect(result.current.flow).toEqual({ step: "summary" });
  });

  it("ends when the session does", async () => {
    const { result, rerender } = await onCodesStep();

    auth.isAuthenticated = false;
    rerender();

    expect(result.current.flow).toEqual({ step: "summary" });
  });
});
