import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { AccountPage } from "./AccountPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { changePassword, ApiError } from "../api/client";

// Keep the real ApiError (errText branches on `instanceof`); stub the network
// and the AuthProvider's registration hooks.
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

// Runtime-generated, policy-shaped — never a literal secret in source.
const freshPassword = () => `Aa1!${crypto.randomUUID()}`;

const WORKER = { sub: "u1", role: "Worker" };

beforeEach(() => vi.resetAllMocks());

function fill(current: string, next: string, confirm: string) {
  fireEvent.change(screen.getByLabelText(/Current password/), { target: { value: current } });
  fireEvent.change(screen.getByLabelText(/New password/), { target: { value: next } });
  fireEvent.change(screen.getByLabelText(/Confirm new password/), { target: { value: confirm } });
}

const submit = () => fireEvent.click(screen.getByRole("button", { name: "Change password" }));

describe("AccountPage (#165 self-service password change)", () => {
  it("changes the password and reports the other devices were signed out", async () => {
    mockChangePassword.mockResolvedValue(undefined);
    renderWithProviders(<AccountPage />, { token: WORKER });

    const current = freshPassword();
    const next = freshPassword();
    fill(current, next, next);
    await act(async () => { submit(); });

    expect(mockChangePassword).toHaveBeenCalledWith(
      { currentPassword: current, newPassword: next });
    expect(await screen.findByText(/signed out/i)).toBeInTheDocument();
    // The form is cleared so the credentials don't linger on screen.
    expect(screen.getByLabelText(/Current password/)).toHaveValue("");
    expect(screen.getByLabelText(/Confirm new password/)).toHaveValue("");
  });

  it("refuses a mismatched confirmation without calling the server", async () => {
    renderWithProviders(<AccountPage />, { token: WORKER });

    fill(freshPassword(), freshPassword(), freshPassword());
    await act(async () => { submit(); });

    expect(mockChangePassword).not.toHaveBeenCalled();
    expect(screen.getByText(/don't match/i)).toBeInTheDocument();
  });

  it("refuses a too-short new password client-side", async () => {
    renderWithProviders(<AccountPage />, { token: WORKER });

    fill(freshPassword(), "Aa1!short", "Aa1!short"); // 9 chars
    await act(async () => { submit(); });

    expect(mockChangePassword).not.toHaveBeenCalled();
    expect(screen.getByText(/at least 12 characters/i)).toBeInTheDocument();
  });

  it("surfaces a server rejection (e.g. wrong current password) and keeps the form filled", async () => {
    mockChangePassword.mockRejectedValue(
      new ApiError(400, "Bad request", "Current password is incorrect."));
    renderWithProviders(<AccountPage />, { token: WORKER });

    const next = freshPassword();
    fill("whatever-wrong", next, next);
    await act(async () => { submit(); });

    expect(await screen.findByText(/Current password is incorrect/)).toBeInTheDocument();
    // Not cleared on failure — the user fixes the current password and retries.
    expect(screen.getByLabelText(/New password/)).toHaveValue(next);
  });

  it("renders for any role — every user can change their own password", async () => {
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(screen.getByRole("button", { name: "Change password" })).toBeInTheDocument();
  });
});
