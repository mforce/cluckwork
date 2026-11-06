import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, renderHook, screen } from "@testing-library/react";
import { RecoveryCodesProvider, useRecoveryCodesSlot } from "./recoveryCodes";

// #1277 review S4 (d) — the slot's own contract, below the UI that disables
// its triggers: while a set is held, saved or not, no new request may start.
// Adapted from the round-5 runner's slot probe.
const auth = vi.hoisted(() => ({ isAuthenticated: true }));
vi.mock("../../auth/useAuth", () => ({ useAuth: () => auth }));

beforeEach(() => { auth.isAuthenticated = true; });

describe("expect()", () => {
  it("refuses a new request while a set is held, unsaved and saved", () => {
    const { result } = renderHook(useRecoveryCodesSlot, { wrapper: RecoveryCodesProvider });
    act(() => {
      const ticket = result.current.expect()!;
      ticket.deliver([crypto.randomUUID()], { regenerated: true, email: "test@farm.local" });
      ticket.release();
    });

    act(() => { expect(result.current.expect()).toBeNull(); });
    fireEvent.click(screen.getByRole("checkbox"));
    act(() => { expect(result.current.expect()).toBeNull(); });
  });
});
