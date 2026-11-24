import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listUsers, removeUserPasskeys, resetUserTwoFactor } from "../api/cluckwork";
import type { User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import i18n from "../i18n";

// #1284 — an Owner removes another user's passkeys (record 1282 decisions 10
// and 18, #1283 "Users A"). The step-up proof cases shared by every Users
// dialog run for this one too, in UsersPage.test.tsx's #1279 caller table.
vi.mock("../api/cluckwork", () => ({
  listUsers: vi.fn(),
  resetUserTwoFactor: vi.fn(),
  removeUserPasskeys: vi.fn(),
  listFlockAssignments: vi.fn(),
  listFlocks: vi.fn(),
  getFlock: vi.fn(),
}));
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, stepUp: vi.fn() };
});

const mockListUsers = vi.mocked(listUsers);
const mockRemove = vi.mocked(removeUserPasskeys);
const mockStepUp = vi.mocked(stepUp);

const PASSWORD = crypto.randomUUID();
const user = (id: string, email: string, passkeyCount: number): User =>
  ({ id, email, displayName: null, role: "Worker", disabledAt: null, twoFactorEnabled: false, passkeyCount });
// The ADMIN token's sub is "u1", so SELF is the actor's own row.
const WITH = { ...user("u-p", "ben@farm.test", 2), displayName: "Ben" };
const WITHOUT = user("u-n", "plain@farm.test", 0);
const SELF = user("u1", "self@farm.test", 1);
const ADMIN = { sub: "u1", role: "Admin" };

const inspector = (email: string) => {
  fireEvent.click(screen.getByRole("row", { name: new RegExp(email) }));
  return within(screen.getByRole("region", { name: "User details" }));
};
const removeAction = (email: string) => inspector(email).queryByRole("button", { name: i18n.t("users:removePasskeysButton") });
const dialog = () => screen.getByRole("dialog");
const t = (key: string, options: Record<string, unknown> = {}) => i18n.t(`users:${key}` as "users:heading", options);

async function openAndSubmit() {
  fireEvent.click(removeAction(WITH.email)!);
  fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: PASSWORD } });
  await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: t("removePasskeysSubmitButton") })); });
}

beforeEach(() => {
  vi.clearAllMocks();
  mockListUsers.mockResolvedValue([WITH, WITHOUT, SELF]);
  mockStepUp.mockResolvedValue({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:05:00Z" });
  mockRemove.mockResolvedValue(undefined);
});

describe("UsersPage passkey removal (#1284)", () => {
  it("offers Remove passkeys only for a user with passkeys who is not the actor", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(WITH.email);

    expect(removeAction(WITH.email)).toBeInTheDocument();
    expect(removeAction(WITHOUT.email)).not.toBeInTheDocument();
    expect(removeAction(SELF.email)).not.toBeInTheDocument();
  });

  it("shows the passkey count in the inspector, and sends the actor to Account for their own", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(WITH.email);

    expect(inspector(WITH.email).getByText(t("passkeysRegistered", { count: 2 }))).toBeInTheDocument();
    expect(inspector(WITHOUT.email).getByText(t("passkeysNone"))).toBeInTheDocument();
    expect(inspector(WITH.email).queryByText(t("ownPasskeysHint"))).not.toBeInTheDocument();
    expect(inspector(SELF.email).getByText(t("ownPasskeysHint"))).toBeInTheDocument();
  });

  it("names the target and says what the removal ends and what it keeps", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(WITH.email);
    fireEvent.click(removeAction(WITH.email)!);

    const confirm = screen.getByRole("dialog", { name: t("removePasskeysTitle", { name: "Ben" }) });
    expect(confirm).toHaveAccessibleDescription(`Ben · ${WITH.email} ${t("removePasskeysWarningBody", { count: 2 })}`);
    expect(within(confirm).getByText(t("removePasskeysNote"))).toBeInTheDocument();
  });

  it("steps up, sends the removal with the grant and a key, then refreshes the list", async () => {
    const grant = crypto.randomUUID();
    mockStepUp.mockResolvedValueOnce({ token: grant, expiresAt: "2099-01-01T00:05:00Z" });
    mockListUsers.mockResolvedValueOnce([WITH, WITHOUT, SELF]).mockResolvedValue([{ ...WITH, passkeyCount: 0 }, WITHOUT, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(WITH.email);

    await openAndSubmit();

    expect(mockStepUp).toHaveBeenCalledWith({ password: PASSWORD });
    expect(mockRemove).toHaveBeenCalledWith(WITH.id, expect.any(String), grant);
    expect(vi.mocked(resetUserTwoFactor)).not.toHaveBeenCalled();
    expect(await screen.findByText(t("passkeysRemovedMessage", { email: WITH.email }))).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(removeAction(WITH.email)).not.toBeInTheDocument();
  });

  it("says the user was not found on a 404", async () => {
    mockRemove.mockRejectedValueOnce(new ApiError(404, "Not Found", "Not Found"));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(WITH.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText(t("resetTwoFactorNotFoundError"))).toBeInTheDocument();
  });
});
