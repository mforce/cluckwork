import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { SetPasswordPage } from "./SetPasswordPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { changePassword, logout, ApiError } from "../api/client";
import i18n from "../i18n";

// Keep the real ApiError (errText branches on `instanceof`); stub the network
// and the AuthProvider's registration hooks — same pattern as AccountPage.test.tsx.
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    changePassword: vi.fn(),
    logout: vi.fn(),
    setOnTokensChanged: vi.fn(),
    setOnUnauthenticated: vi.fn(),
  };
});

const mockChangePassword = vi.mocked(changePassword);
const mockLogout = vi.mocked(logout);

// Runtime-generated, policy-shaped — never a literal secret in source.
const freshPassword = () => `Aa1!${crypto.randomUUID()}`;

// #283 — the claim SetPasswordPage itself doesn't read (ProtectedRoute does,
// separately), but a real first-run session always carries it; the page's own
// behavior doesn't depend on it either way.
const PENDING_ADMIN = { sub: "u1", role: "Admin", must_change_password: "true" };

beforeEach(() => vi.resetAllMocks());

function fill(temporary: string, next: string, confirm: string) {
  fireEvent.change(screen.getByLabelText(/Temporary password/), { target: { value: temporary } });
  fireEvent.change(screen.getByLabelText(/New password/), { target: { value: next } });
  fireEvent.change(screen.getByLabelText(/Confirm new password/), { target: { value: confirm } });
}

const submit = () => fireEvent.click(screen.getByRole("button", { name: "Set password" }));

describe("SetPasswordPage (#283 first-run set-your-password screen)", () => {
  it("submits the temporary + new password to the SAME change-password endpoint AccountPage uses", async () => {
    mockChangePassword.mockResolvedValue(undefined);
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    const temporary = freshPassword();
    const next = freshPassword();
    fill(temporary, next, next);
    await act(async () => { submit(); });

    expect(mockChangePassword).toHaveBeenCalledWith(
      { currentPassword: temporary, newPassword: next });
  });

  it("refuses a mismatched confirmation without calling the server", async () => {
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    fill(freshPassword(), freshPassword(), freshPassword());
    await act(async () => { submit(); });

    expect(mockChangePassword).not.toHaveBeenCalled();
    expect(screen.getByText(/don't match/i)).toBeInTheDocument();
  });

  it("refuses a too-short new password client-side", async () => {
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    fill(freshPassword(), "Aa1!short", "Aa1!short"); // 9 chars, under the 12-char floor
    await act(async () => { submit(); });

    expect(mockChangePassword).not.toHaveBeenCalled();
    expect(screen.getByText(/at least 12 characters/i)).toBeInTheDocument();
  });

  it("surfaces a server rejection (e.g. wrong temporary password) and keeps the form filled", async () => {
    mockChangePassword.mockRejectedValue(
      new ApiError(400, "Bad request", "Current password is incorrect."));
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    const next = freshPassword();
    fill("whatever-wrong", next, next);
    await act(async () => { submit(); });

    expect(await screen.findByText(/Current password is incorrect/)).toBeInTheDocument();
    // Not cleared on failure — the user fixes the temporary password and retries.
    expect(screen.getByLabelText(/New password/)).toHaveValue(next);
  });

  it("a same-tick double submit is skipped (usePendingAction's ref guard), not double-called", async () => {
    mockChangePassword.mockResolvedValue(undefined);
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    const next = freshPassword();
    fill(freshPassword(), next, next);
    await act(async () => {
      submit();
      submit();
    });

    expect(mockChangePassword).toHaveBeenCalledTimes(1);
  });

  it("the sign-out escape hatch calls logout — a gated user is never stuck on this screen", async () => {
    mockLogout.mockResolvedValue(undefined);
    renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign out" }));
    });

    expect(mockLogout).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#283, mirrors the AccountPage i18n-wiring block)
// ---------------------------------------------------------------------------

describe("SetPasswordPage i18n wiring (#283)", () => {
  function withOverride(key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", "auth", key) as string;
    i18n.addResource("en", "auth", key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", "auth", key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("setPasswordHeading", "HEADING-MARKER", async () => {
      renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Set your password" })).not.toBeInTheDocument();
    });
  });

  it("reads the hint prose from the catalog, not a hardcoded literal", async () => {
    await withOverride("setPasswordHint", "HINT-MARKER", async () => {
      renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });
      expect(screen.getByText("HINT-MARKER")).toBeInTheDocument();
    });
  });

  it("interpolates {{min}} into the new-password label from the catalog", async () => {
    await withOverride("setPasswordNewLabel", "NEW-PW-MARKER {{min}} MARKER-END", async () => {
      renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });
      expect(screen.getByLabelText("NEW-PW-MARKER 12 MARKER-END")).toBeInTheDocument();
    });
  });

  it("reads the submit button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("setPasswordButton", "SUBMIT-MARKER", async () => {
      renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });
      expect(screen.getByRole("button", { name: "SUBMIT-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Set password" })).not.toBeInTheDocument();
    });
  });

  it("reads the mismatch error from the catalog, not a hardcoded literal", async () => {
    await withOverride("setPasswordMismatchError", "MISMATCH-MARKER", async () => {
      renderWithProviders(<SetPasswordPage />, { token: PENDING_ADMIN });
      fill(freshPassword(), freshPassword(), freshPassword());
      await act(async () => { submit(); });
      expect(screen.getByText("MISMATCH-MARKER")).toBeInTheDocument();
    });
  });
});
