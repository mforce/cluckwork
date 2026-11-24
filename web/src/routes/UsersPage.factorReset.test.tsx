import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { listUsers, removeUserPasskeys, resetUserTwoFactor } from "../api/cluckwork";
import type { Me, User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import { parseError } from "../api/errors";
import i18n from "../i18n";

// #1280 — an Owner resets another user's two-factor (#1274 decision 17, G5.2's
// SPA part). #1284 — and removes their passkeys, a separate action (record 1282
// decision 10). The step-up proof cases shared by every Users dialog run for
// both too, in UsersPage.test.tsx's #1279 caller table.

// Only the resets' paths run here; the other exports the page imports stay stubs.
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
const mockStepUp = vi.mocked(stepUp);

const PASSWORD = crypto.randomUUID();
const user = (id: string, email: string, twoFactorEnabled: boolean, passkeyCount = 0): User =>
  ({ id, email, displayName: null, role: "Worker", disabledAt: null, twoFactorEnabled, passkeyCount });
// The ADMIN token's sub is "u1", so SELF is the actor's own row. ENROLLED has
// both factors, so every kind is offered for it.
const ENROLLED = { ...user("u-e", "enrolled@farm.test", true, 2), displayName: "Ben" };
const UNENROLLED = user("u-n", "plain@farm.test", false);
const SELF = user("u1", "self@farm.test", true, 1);
const ADMIN = { sub: "u1", role: "Admin" };
const OWNER_ME: Me = {
  id: "u1", email: "owner@farm.test", name: null, role: "Admin", language: null, preferredStepperUnit: null,
};
const t = (key: string, options: Record<string, unknown> = {}) => i18n.t(key as "users:heading", options);

// Each kind as the page shows it: the inspector action, the dialog's title,
// warning and submit, the request, and the target once the factor is gone.
const KINDS = [
  {
    kind: "two-factor reset (#1280)",
    action: "reset two-factor",
    submit: "Reset two-factor",
    title: () => t("users:resetTwoFactorTitle"),
    warning: () => t("users:resetTwoFactorWarningBody"),
    note: false,
    send: () => vi.mocked(resetUserTwoFactor),
    done: "users:twoFactorResetMessage",
    notFound: "users:resetTwoFactorNotFoundError",
    own: "Users.CannotResetOwnTwoFactor",
    cleared: { ...ENROLLED, twoFactorEnabled: false },
  },
  {
    kind: "passkey removal (#1284)",
    action: t("users:removePasskeysButton"),
    submit: t("users:removePasskeysSubmitButton"),
    title: () => t("users:removePasskeysTitle", { name: "Ben" }),
    warning: () => t("users:removePasskeysWarningBody", { count: 2 }),
    note: true,
    send: () => vi.mocked(removeUserPasskeys),
    done: "users:passkeysRemovedMessage",
    notFound: "users:removePasskeysNotFoundError",
    own: "Users.CannotRemoveOwnPasskeys",
    cleared: { ...ENROLLED, passkeyCount: 0 },
  },
] as const;

const inspector = (email: string) => {
  fireEvent.click(screen.getByRole("row", { name: new RegExp(email) }));
  return within(screen.getByRole("region", { name: "User details" }));
};
const dialog = () => screen.getByRole("dialog");

beforeEach(() => {
  vi.clearAllMocks();
  mockListUsers.mockResolvedValue([ENROLLED, UNENROLLED, SELF]);
  mockStepUp.mockResolvedValue({ token: "grant-1280", expiresAt: "2026-01-01T00:05:00Z" });
  vi.mocked(resetUserTwoFactor).mockResolvedValue(undefined);
  vi.mocked(removeUserPasskeys).mockResolvedValue(undefined);
});

describe("UsersPage two-factor reset (#1280)", () => {
  const resetAction = (email: string) => inspector(email).queryByRole("button", { name: "reset two-factor" });

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
    expect(resetAction(ENROLLED.email)).toBeInTheDocument();
  });
});

describe("UsersPage passkey removal (#1284)", () => {
  it("shows the passkey count in the inspector, and sends the actor to Account for their own", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    expect(inspector(ENROLLED.email).getByText(t("users:passkeysRegistered", { count: 2 }))).toBeInTheDocument();
    expect(inspector(UNENROLLED.email).getByText(t("users:passkeysNone"))).toBeInTheDocument();
    expect(inspector(ENROLLED.email).queryByText(t("users:ownPasskeysHint"))).not.toBeInTheDocument();
    expect(inspector(SELF.email).getByText(t("users:ownPasskeysHint"))).toBeInTheDocument();
  });

  it("names the one passkey in the singular", async () => {
    mockListUsers.mockResolvedValue([{ ...ENROLLED, passkeyCount: 1 }, UNENROLLED, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(inspector(ENROLLED.email).getByRole("button", { name: t("users:removePasskeysButton") }));

    expect(within(dialog()).getByText(t("users:removePasskeysWarningBody", { count: 1 }))).toBeInTheDocument();
  });
});

describe.each(KINDS)("UsersPage $kind", (k) => {
  const action = (email: string) => inspector(email).queryByRole("button", { name: k.action });
  const mockSend = () => k.send();
  async function submit() {
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: k.submit })); });
  }
  async function openAndSubmit() {
    fireEvent.click(action(ENROLLED.email)!);
    await submit();
  }
  const keyOf = (call: number) => mockSend().mock.calls[call]![1];
  const successMessage = () => t(k.done, { email: ENROLLED.email });

  it("is offered only for a user with that factor who is not the actor", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    expect(action(ENROLLED.email)).toBeInTheDocument();
    expect(action(UNENROLLED.email)).not.toBeInTheDocument();
    expect(action(SELF.email)).not.toBeInTheDocument();
  });

  it("describes the confirmation by its target, then what it ends, and only the passkey removal adds its note", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(action(ENROLLED.email)!);

    const confirm = screen.getByRole("dialog", { name: k.title() });
    expect(confirm).toHaveAccessibleDescription(`Ben · ${ENROLLED.email} ${k.warning()}`);
    expect(within(confirm).queryByText(t("users:removePasskeysNote")) !== null).toBe(k.note);
  });

  it("names the target and the signed-in Owner who confirms it", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN, me: OWNER_ME });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(action(ENROLLED.email)!);

    expect(within(dialog()).getByText(`Ben · ${ENROLLED.email}`)).toBeInTheDocument();
    expect(within(dialog()).getByRole("heading", { name: t("users:confirmItsYouHeading") })).toBeInTheDocument();
    expect(within(dialog()).getByText(t("users:signedInAsCaption", { email: "owner@farm.test" }))).toBeInTheDocument();
  });

  it("still offers the proof, with no signed-in caption, when /me failed", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN, me: null });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(action(ENROLLED.email)!);

    expect(within(dialog()).getByRole("heading", { name: t("users:confirmItsYouHeading") })).toBeInTheDocument();
    expect(within(dialog()).getByLabelText(/Your current password/)).toBeInTheDocument();
    const caption = t("users:signedInAsCaption", { email: "" }).trim();
    expect(within(dialog()).queryByText(caption, { exact: false })).not.toBeInTheDocument();
  });

  it("wraps the identity line and the signed-in caption anywhere", async () => {
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(action(ENROLLED.email)!);

    expect(within(dialog()).getByText(`Ben · ${ENROLLED.email}`)).toHaveStyle({ overflowWrap: "anywhere" });
    expect(within(dialog()).getByText(t("users:signedInAsCaption", { email: "test@farm.local" })))
      .toHaveStyle({ overflowWrap: "anywhere" });
  });

  it("names only the email of a target with no display name", async () => {
    mockListUsers.mockResolvedValue([{ ...ENROLLED, displayName: null }, UNENROLLED, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);
    fireEvent.click(action(ENROLLED.email)!);

    expect(within(dialog()).getByText(ENROLLED.email, { selector: "p" })).toBeInTheDocument();
  });

  it("steps up, sends the request with the grant, then refreshes the list", async () => {
    mockListUsers
      .mockResolvedValueOnce([ENROLLED, UNENROLLED, SELF])
      .mockResolvedValueOnce([k.cleared, UNENROLLED, SELF]);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(mockStepUp).toHaveBeenCalledWith({ password: PASSWORD });
    expect(mockSend()).toHaveBeenCalledTimes(1);
    const [id, key, token] = mockSend().mock.calls[0]!;
    expect([id, token]).toEqual([ENROLLED.id, "grant-1280"]);
    expect(key).toEqual(expect.any(String));
    expect(mockStepUp.mock.invocationCallOrder[0]).toBeLessThan(mockSend().mock.invocationCallOrder[0]!);
    expect(mockListUsers).toHaveBeenCalledTimes(2);
    expect(await screen.findByText(successMessage())).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(action(ENROLLED.email)).not.toBeInTheDocument();
  });

  it("shows a step-up refusal in the dialog and sends nothing", async () => {
    mockStepUp.mockRejectedValue(new ApiError(400, "Users.CurrentPasswordIncorrect", "The current password is incorrect."));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText("The current password is incorrect.")).toBeInTheDocument();
    expect(mockSend()).not.toHaveBeenCalled();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  // Built by the real parseError, so the catalog lookup it does is the one
  // under test. A 404 has no problem body.
  it.each([
    [k.own, 400, `errors:${k.own}`],
    ["Auth.Forbidden", 403, "errors:Auth.Forbidden"],
    ["Identity.StepUpRequired", 403, "errors:Identity.StepUpRequired"],
    ["", 404, k.notFound],
  ] as const)("words a %s %s answer and keeps the dialog open", async (title, status, key) => {
    const body = title ? JSON.stringify({ title, status, detail: "server English" }) : null;
    mockSend().mockRejectedValue(await parseError(new Response(body, { status, statusText: "server English" })));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText(t(key))).toBeInTheDocument();
    expect(within(dialog()).queryByText("server English")).not.toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  it("keeps the server's text for an answer it does not word", async () => {
    mockSend().mockRejectedValue(new ApiError(422, "Users.Something", "server English"));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();

    expect(await within(dialog()).findByText("server English")).toBeInTheDocument();
  });

  it("sends nothing when the dialog is dismissed while the grant is issued", async () => {
    let grant!: (value: { token: string; expiresAt: string }) => void;
    mockStepUp.mockReturnValue(new Promise((resolve) => { grant = resolve; }));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => { grant({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" }); });

    expect(mockSend()).not.toHaveBeenCalled();
    expect(mockListUsers).toHaveBeenCalledTimes(1);
  });

  // The refresh below keeps listing the target with the factor, so a second
  // request for the same user runs under the same key scope.
  it("replays the same key after a failure, and rotates it after success", async () => {
    mockSend().mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    expect(await within(dialog()).findByText("boom")).toBeInTheDocument();
    await submit();
    expect(await screen.findByText(successMessage())).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await openAndSubmit();

    expect(mockSend()).toHaveBeenCalledTimes(3);
    expect(keyOf(1)).toBe(keyOf(0));
    expect(keyOf(2)).not.toBe(keyOf(1));
  });

  it("rotates the key and refreshes, but reports nothing, when the dialog is dismissed during the request", async () => {
    let answer!: () => void;
    mockSend().mockReturnValueOnce(new Promise((resolve) => { answer = () => resolve(undefined); }));
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

  it("sends nothing when the Users page unmounts while the grant is issued", async () => {
    let grant!: (value: { token: string; expiresAt: string }) => void;
    mockStepUp.mockReturnValue(new Promise((resolve) => { grant = resolve; }));
    const view = renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    view.unmount();
    await act(async () => { grant({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" }); });

    expect(mockSend()).not.toHaveBeenCalled();
  });

  it("still finishes a request already sent when the Users page unmounts", async () => {
    let answer!: () => void;
    mockSend().mockReturnValue(new Promise((resolve) => { answer = () => resolve(undefined); }));
    const view = renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText(ENROLLED.email);

    await openAndSubmit();
    expect(mockSend()).toHaveBeenCalledTimes(1);
    view.unmount();
    await act(async () => { answer(); });

    expect(mockListUsers).toHaveBeenCalledTimes(2);
  });
});
