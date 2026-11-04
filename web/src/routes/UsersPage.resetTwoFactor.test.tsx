import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listUsers, resetUserTwoFactor } from "../api/cluckwork";
import type { User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import { parseError } from "../api/errors";
import i18n from "../i18n";

// #1280 — an Owner resets another user's two-factor (#1274 decision 17, G5.2's
// SPA part). The step-up proof cases shared by every Users dialog run for this
// one too, in UsersPage.test.tsx's #1279 caller table.

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
const ENROLLED = user("u-e", "enrolled@farm.test", true);
const UNENROLLED = user("u-n", "plain@farm.test", false);
const SELF = user("u1", "self@farm.test", true);
const ADMIN = { sub: "u1", role: "Admin" };

const inspector = (email: string) => {
  fireEvent.click(screen.getByRole("row", { name: new RegExp(email) }));
  return within(screen.getByRole("region", { name: "User details" }));
};
const resetAction = (email: string) => inspector(email).queryByRole("button", { name: "reset two-factor" });
const dialog = () => screen.getByRole("dialog");

async function openAndSubmit() {
  fireEvent.click(resetAction(ENROLLED.email)!);
  fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: PASSWORD } });
  await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Reset two-factor" })); });
}

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

  it("shows whether two-factor is on in the inspector", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    expect(inspector(ENROLLED.email).getByText("On")).toBeInTheDocument();
    expect(inspector(UNENROLLED.email).getByText("Off")).toBeInTheDocument();
  });

  it("says in the confirmation what the reset ends", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(resetAction(ENROLLED.email)!);

    const confirm = screen.getByRole("dialog", { name: `Reset two-factor — ${ENROLLED.email}` });
    expect(confirm).toHaveAccessibleDescription(i18n.t("users:resetTwoFactorWarningBody"));
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
