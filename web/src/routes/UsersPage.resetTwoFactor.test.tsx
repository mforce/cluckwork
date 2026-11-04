import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listUsers, resetUserTwoFactor } from "../api/cluckwork";
import type { Me, User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import { parseError } from "../api/errors";
import i18n from "../i18n";

// #1280 — an Owner resets another user's two-factor (#1274 decision 17, G5.2's
// SPA part). The step-up proof cases shared by every Users dialog run for this
// one too, in UsersPage.test.tsx's #1279 caller table.

// Only the reset's paths run here; the other exports the page imports stay stubs.
vi.mock("../api/cluckwork", () => ({
  listUsers: vi.fn(),
  resetUserTwoFactor: vi.fn(),
  listFlockAssignments: vi.fn(),
  listFlocks: vi.fn(),
  getFlock: vi.fn(),
}));
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, stepUp: vi.fn() };
});

const mockListUsers = vi.mocked(listUsers);
const mockReset = vi.mocked(resetUserTwoFactor);
const mockStepUp = vi.mocked(stepUp);

const PASSWORD = crypto.randomUUID();
const user = (id: string, email: string, twoFactorEnabled: boolean): User =>
  ({ id, email, displayName: null, role: "Worker", disabledAt: null, twoFactorEnabled });
// The ADMIN token's sub is "u1", so SELF is the actor's own row.
const ENROLLED = { ...user("u-e", "enrolled@farm.test", true), displayName: "Ben" };
const UNENROLLED = user("u-n", "plain@farm.test", false);
const SELF = user("u1", "self@farm.test", true);
const ADMIN = { sub: "u1", role: "Admin" };
const OWNER_ME: Me = {
  id: "u1", email: "owner@farm.test", name: null, role: "Admin", language: null, preferredStepperUnit: null,
};

const inspector = (email: string) => {
  fireEvent.click(screen.getByRole("row", { name: new RegExp(email) }));
  return within(screen.getByRole("region", { name: "User details" }));
};
const resetAction = (email: string) => inspector(email).queryByRole("button", { name: "reset two-factor" });
const dialog = () => screen.getByRole("dialog");

async function submit() {
  fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: PASSWORD } });
  await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Reset two-factor" })); });
}
async function openAndSubmit() {
  fireEvent.click(resetAction(ENROLLED.email)!);
  await submit();
}
const keyOf = (call: number) => mockReset.mock.calls[call]![1];
const successMessage = () => i18n.t("users:twoFactorResetMessage", { email: ENROLLED.email });

beforeEach(() => {
  vi.clearAllMocks();
  mockListUsers.mockResolvedValue([ENROLLED, UNENROLLED, SELF]);
  mockStepUp.mockResolvedValue({ token: "grant-1280", expiresAt: "2026-01-01T00:05:00Z" });
  mockReset.mockResolvedValue(undefined);
});

describe("UsersPage two-factor reset (#1280)", () => {
  it("offers the reset only for an enrolled user who is not the actor", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    expect(resetAction(ENROLLED.email)).toBeInTheDocument();
    expect(resetAction(UNENROLLED.email)).not.toBeInTheDocument();
    expect(resetAction(SELF.email)).not.toBeInTheDocument();
  });

  it("offers the reset and enable, but not disable, for a disabled user with two-factor on", async () => {
    const disabled = { ...user("u-d", "disabled@farm.test", true), disabledAt: "2026-08-01T00:00:00Z" };
    mockListUsers.mockResolvedValue([ENROLLED, disabled, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(disabled.email);

    const actions = inspector(disabled.email);
    expect(actions.getByRole("button", { name: "reset two-factor" })).toBeInTheDocument();
    expect(actions.getByRole("button", { name: "enable" })).toBeInTheDocument();
    expect(actions.queryByRole("button", { name: "disable" })).not.toBeInTheDocument();
  });

  it("shows whether two-factor is on in the inspector", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    expect(inspector(ENROLLED.email).getByText("On")).toBeInTheDocument();
    expect(inspector(UNENROLLED.email).getByText("Off")).toBeInTheDocument();
  });

  it("describes the confirmation by its target, then what the reset ends", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    const confirm = screen.getByRole("dialog", { name: "Reset two-factor authentication?" });
    expect(confirm).toHaveAccessibleDescription(`Ben · ${ENROLLED.email} ${i18n.t("users:resetTwoFactorWarningBody")}`);
  });

  it("names the target and the signed-in Owner who confirms the reset", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN, me: OWNER_ME });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    expect(within(dialog()).getByText(`Ben · ${ENROLLED.email}`)).toBeInTheDocument();
    expect(within(dialog()).getByRole("heading", { name: i18n.t("users:confirmItsYouHeading") })).toBeInTheDocument();
    expect(within(dialog()).getByText(i18n.t("users:signedInAsCaption", { email: "owner@farm.test" }))).toBeInTheDocument();
  });

  it("still offers the proof, with no signed-in caption, when /me failed", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN, me: null });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    expect(within(dialog()).getByRole("heading", { name: i18n.t("users:confirmItsYouHeading") })).toBeInTheDocument();
    expect(within(dialog()).getByLabelText(/Your current password/)).toBeInTheDocument();
    const caption = i18n.t("users:signedInAsCaption", { email: "" }).trim();
    expect(within(dialog()).queryByText(caption, { exact: false })).not.toBeInTheDocument();
  });

  it("lets a long target email wrap inside the dialog", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    expect(within(dialog()).getByText(`Ben · ${ENROLLED.email}`)).toHaveStyle({ overflowWrap: "anywhere" });
    expect(within(dialog()).getByText(i18n.t("users:signedInAsCaption", { email: "test@farm.local" })))
      .toHaveStyle({ overflowWrap: "anywhere" });
  });

  it("names only the email of a target with no display name", async () => {
    mockListUsers.mockResolvedValue([{ ...ENROLLED, displayName: null }, UNENROLLED, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    expect(within(dialog()).getByText(ENROLLED.email, { selector: "p" })).toBeInTheDocument();
  });

  it("steps up, sends the DELETE with the grant, then refreshes the list", async () => {
    mockListUsers
      .mockResolvedValueOnce([ENROLLED, UNENROLLED, SELF])
      .mockResolvedValueOnce([{ ...ENROLLED, twoFactorEnabled: false }, UNENROLLED, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(mockStepUp).toHaveBeenCalledWith({ password: PASSWORD });
    expect(mockReset).toHaveBeenCalledTimes(1);
    const [id, key, token] = mockReset.mock.calls[0]!;
    expect([id, token]).toEqual([ENROLLED.id, "grant-1280"]);
    expect(key).toEqual(expect.any(String));
    expect(mockStepUp.mock.invocationCallOrder[0]).toBeLessThan(mockReset.mock.invocationCallOrder[0]!);
    expect(mockListUsers).toHaveBeenCalledTimes(2);
    expect(await screen.findByText(i18n.t("users:twoFactorResetMessage", { email: ENROLLED.email }))).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(resetAction(ENROLLED.email)).not.toBeInTheDocument();
  });

  it("shows a step-up refusal in the dialog and sends no DELETE", async () => {
    mockStepUp.mockRejectedValue(new ApiError(400, "Users.CurrentPasswordIncorrect", "The current password is incorrect."));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText("The current password is incorrect.")).toBeInTheDocument();
    expect(mockReset).not.toHaveBeenCalled();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  // Built by the real parseError, so the catalog lookup it does is the one
  // under test. A 404 has no problem body.
  it.each([
    ["Users.CannotResetOwnTwoFactor", 400, "errors:Users.CannotResetOwnTwoFactor"],
    ["Auth.Forbidden", 403, "errors:Auth.Forbidden"],
    ["Identity.StepUpRequired", 403, "errors:Identity.StepUpRequired"],
    ["", 404, "users:resetTwoFactorNotFoundError"],
  ] as const)("words a %s %s answer and keeps the dialog open", async (title, status, key) => {
    const body = title ? JSON.stringify({ title, status, detail: "server English" }) : null;
    mockReset.mockRejectedValue(await parseError(new Response(body, { status, statusText: "server English" })));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText(i18n.t(key))).toBeInTheDocument();
    expect(within(dialog()).queryByText("server English")).not.toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  it("keeps the server's text for an answer it does not word", async () => {
    mockReset.mockRejectedValue(new ApiError(422, "Users.Something", "server English"));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText("server English")).toBeInTheDocument();
  });

  it("sends no DELETE when the dialog is dismissed while the grant is issued", async () => {
    let grant!: (value: { token: string; expiresAt: string }) => void;
    mockStepUp.mockReturnValue(new Promise((resolve) => { grant = resolve; }));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { grant({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" }); });

    expect(mockReset).not.toHaveBeenCalled();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  // The refresh below keeps listing the target as enrolled, so a second reset
  // of the same user runs under the same key scope.
  it("replays the same key after a failure, and rotates it after success", async () => {
    mockReset.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    expect(await within(dialog()).findByText("boom")).toBeInTheDocument();
    await submit();
    expect(await screen.findByText(successMessage())).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await openAndSubmit();

    expect(mockReset).toHaveBeenCalledTimes(3);
    expect(keyOf(1)).toBe(keyOf(0));
    expect(keyOf(2)).not.toBe(keyOf(1));
  });

  it("rotates the key and refreshes, but reports nothing, when the dialog is dismissed during the DELETE", async () => {
    let answer!: () => void;
    mockReset.mockReturnValueOnce(new Promise((resolve) => { answer = () => resolve(undefined); }));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { answer(); });

    expect(screen.queryByText(successMessage())).not.toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await openAndSubmit();
    expect(keyOf(1)).not.toBe(keyOf(0));
  });

  it("sends no DELETE when the Users page unmounts while the grant is issued", async () => {
    let grant!: (value: { token: string; expiresAt: string }) => void;
    mockStepUp.mockReturnValue(new Promise((resolve) => { grant = resolve; }));
    const view = renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    view.unmount();
    await act(async () => { grant({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" }); });

    expect(mockReset).not.toHaveBeenCalled();
  });

  it("still finishes a DELETE already sent when the Users page unmounts", async () => {
    let answer!: () => void;
    mockReset.mockReturnValue(new Promise((resolve) => { answer = () => resolve(undefined); }));
    const view = renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    expect(mockReset).toHaveBeenCalledTimes(1);
    view.unmount();
    await act(async () => { answer(); });

    expect(mockListUsers).toHaveBeenCalledTimes(2);
  });
});
