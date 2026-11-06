import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, renderHook, screen, waitFor } from "@testing-library/react";
import type { FormEvent } from "react";
import { useTwoFactor } from "./useTwoFactor";
import { RecoveryCodesProvider } from "./recoveryCodes";
import { getTwoFactorStatus, regenerateRecoveryCodes } from "../../api/cluckwork";
import { stepUp } from "../../api/client";

// #1277 — the hook hands new recovery codes to the slot above the routes and
// never keeps them itself. JSON.stringify sees only the
// serializable part of the hook's result (its state, not its closures), which
// is where a kept set would sit.
const auth = vi.hoisted(() => ({ isAuthenticated: true }));
vi.mock("../../auth/useAuth", () => ({ useAuth: () => auth }));
vi.mock("../../api/cluckwork", async (original) => ({
  ...await original<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(), regenerateRecoveryCodes: vi.fn(),
}));
vi.mock("../../api/client", async (original) => ({
  ...await original<typeof import("../../api/client")>(), stepUp: vi.fn(),
}));

let code: string;

beforeEach(() => {
  code = crypto.randomUUID();
  auth.isAuthenticated = true;
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
  vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: [code] });
  vi.mocked(stepUp).mockResolvedValue({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
});

describe("new recovery codes", () => {
  it("go to the slot, which shows them, and never into the hook's state", async () => {
    const { result } = renderHook(useTwoFactor, { wrapper: RecoveryCodesProvider });
    await waitFor(() => expect(result.current.status.data?.enabled).toBe(true));
    act(() => result.current.openProof("regenerate"));
    act(() => result.current.setProof({ password: crypto.randomUUID() }));

    await act(async () => { await result.current.onProof({ preventDefault() {} } as FormEvent); });

    expect(await screen.findByText(code)).toBeInTheDocument();
    expect(JSON.stringify(result.current)).not.toContain(code);
    expect(result.current.flow).toEqual({ step: "summary" });
  });
});
