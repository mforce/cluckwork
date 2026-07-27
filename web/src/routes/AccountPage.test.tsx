import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { AccountPage } from "./AccountPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { changePassword, ApiError } from "../api/client";
import i18n from "../i18n";

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

  it("shows the Preferences section with the language selector now that more than one language pack is installed (#182)", async () => {
    renderWithProviders(<AccountPage />, { token: WORKER });
    expect(screen.getByRole("heading", { name: "Preferences" })).toBeInTheDocument();
    expect(screen.getByLabelText("Language")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 25, batch B4)
// ---------------------------------------------------------------------------

// `account` IS in TRANSLATED_NAMESPACES (unlike most B4 screens), but these
// tests still run under the default English locale, so asserting the plain
// English string would prove nothing beyond "the fallback still works" (the
// same CONTRIBUTING-i18n.md fallback trap as the English-first screens).
// Swap the catalog value at runtime instead, the same i18n.addResource
// technique the other batches use, so each marker only renders if the
// component actually reads the catalog rather than a literal that happens to
// still match it.
describe("AccountPage i18n wiring (#182, Task 25)", () => {
  function withOverride(key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", "account", key) as string;
    i18n.addResource("en", "account", key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", "account", key, original);
    });
  }

  // Distinct from the file's WORKER fixture: claims.ts's currentUserRole()
  // only recognizes "Admin"/"Manager"/"Sales"/"ReadOnly" as explicit role
  // claims — a "Worker" claim value isn't one of them, so it actually decodes
  // to "Denied" (any unrecognized claim), not "Worker" (which means NO claim
  // at all). "ReadOnly" is used here so the rendered role text in these two
  // tests is the literal, unsurprising string the assertion names.
  const READ_ONLY = { sub: "u2", role: "ReadOnly" };

  it("reads the page heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("heading", "HEADING-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Account" })).not.toBeInTheDocument();
    });
  });

  it("reads the JSX-interleaved role line from the catalog via <Trans>, interpolating {{role}}", async () => {
    await withOverride("roleLine", "ROLE-MARKER {{role}} MARKER-END", async () => {
      renderWithProviders(<AccountPage />, { token: READ_ONLY });
      expect(screen.getByText("ROLE-MARKER ReadOnly MARKER-END")).toBeInTheDocument();
      expect(screen.queryByText(/signed in with the/)).not.toBeInTheDocument();
    });
  });

  it("wraps the role in a real <strong> element via the <Trans> components mapping", async () => {
    renderWithProviders(<AccountPage />, { token: READ_ONLY });
    expect(screen.getByText("ReadOnly").tagName).toBe("STRONG");
  });

  it("reads the change-password heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("changePasswordHeading", "CHANGE-PW-HEADING-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(
        screen.getByRole("heading", { name: "CHANGE-PW-HEADING-MARKER" }),
      ).toBeInTheDocument();
    });
  });

  it("reads the change-password hint prose from the catalog, not a hardcoded literal", async () => {
    await withOverride("changePasswordHint", "CHANGE-PW-HINT-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(screen.getByText("CHANGE-PW-HINT-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/signs you out everywhere else/)).not.toBeInTheDocument();
    });
  });

  it("reads the current-password label from the catalog, not a hardcoded literal", async () => {
    await withOverride("currentPasswordLabel", "CURRENT-PW-MARKER *", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(screen.getByLabelText(/CURRENT-PW-MARKER/)).toBeInTheDocument();
      expect(screen.queryByLabelText(/^Current password/)).not.toBeInTheDocument();
    });
  });

  it("interpolates {{min}} into the new-password label from the catalog", async () => {
    await withOverride("newPasswordLabel", "NEW-PW-MARKER {{min}} MARKER-END", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      // MIN_LENGTH is 12 (AccountPage.tsx) — asserting the exact number, not
      // just that A number appears, is what would catch a mutation that
      // dropped the interpolation and always rendered a literal "12".
      expect(screen.getByLabelText("NEW-PW-MARKER 12 MARKER-END")).toBeInTheDocument();
      expect(screen.queryByLabelText(/^New password/)).not.toBeInTheDocument();
    });
  });

  it("reads the confirm-password label from the catalog, not a hardcoded literal", async () => {
    await withOverride("confirmPasswordLabel", "CONFIRM-PW-MARKER *", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(screen.getByLabelText(/CONFIRM-PW-MARKER/)).toBeInTheDocument();
      expect(screen.queryByLabelText(/^Confirm new password/)).not.toBeInTheDocument();
    });
  });

  it("reads the submit button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("changePasswordButton", "SUBMIT-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      expect(screen.getByRole("button", { name: "SUBMIT-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Change password" })).not.toBeInTheDocument();
    });
  });

  it("reads the mismatch error from the catalog, not a hardcoded literal", async () => {
    await withOverride("passwordMismatchError", "MISMATCH-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      fill(freshPassword(), freshPassword(), freshPassword());
      await act(async () => { submit(); });
      expect(screen.getByText("MISMATCH-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/don't match/)).not.toBeInTheDocument();
    });
  });

  it("interpolates {{min}} into the too-short error from the catalog", async () => {
    await withOverride("passwordTooShortError", "TOO-SHORT-MARKER {{min}} MARKER-END", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      fill(freshPassword(), "Aa1!short", "Aa1!short"); // 9 chars, under MIN_LENGTH (12)
      await act(async () => { submit(); });
      expect(screen.getByText("TOO-SHORT-MARKER 12 MARKER-END")).toBeInTheDocument();
      expect(screen.queryByText(/at least 12 characters/)).not.toBeInTheDocument();
    });
  });

  it("reads the success message from the catalog, not a hardcoded literal", async () => {
    mockChangePassword.mockResolvedValue(undefined);
    await withOverride("passwordChangedMessage", "SUCCESS-MARKER", async () => {
      renderWithProviders(<AccountPage />, { token: WORKER });
      const next = freshPassword();
      fill(freshPassword(), next, next);
      await act(async () => { submit(); });
      expect(await screen.findByText("SUCCESS-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/signed out/)).not.toBeInTheDocument();
    });
  });
});
