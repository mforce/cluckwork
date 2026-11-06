import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, renderHook, screen } from "@testing-library/react";
import { RecoveryCodesProvider, useRecoveryCodesSlot } from "./recoveryCodes";
import { logout } from "../../api/session";

// #1277 — the slot's own contract, below the UI that disables its triggers:
// while a set is held, saved or not, no new request may start.
const auth = vi.hoisted(() => ({ isAuthenticated: true }));
vi.mock("../../auth/useAuth", () => ({ useAuth: () => auth }));

beforeEach(() => { auth.isAuthenticated = true; });

describe("expect()", () => {
  it("refuses a request once sign-out has started", () => {
    vi.mocked(fetch).mockReturnValue(new Promise<Response>(() => undefined));
    const { result } = renderHook(useRecoveryCodesSlot, { wrapper: RecoveryCodesProvider });
    // The floor: before sign-out a request starts (and ends at once).
    act(() => {
      const before = result.current.expect();
      expect(before).not.toBeNull();
      before!.release();
    });

    void logout();

    act(() => { expect(result.current.expect()).toBeNull(); });
  });

  it("refuses a second request while the first is pending", () => {
    const { result } = renderHook(useRecoveryCodesSlot, { wrapper: RecoveryCodesProvider });
    act(() => {
      const first = result.current.expect();
      expect(first).not.toBeNull();
      expect(result.current.expect()).toBeNull();
      first!.release();
    });
  });

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
