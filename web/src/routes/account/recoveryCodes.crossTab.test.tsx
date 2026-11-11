import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { MAIN_CONTENT_ID } from "../../lib/mainContent";
import { setStoredToken } from "../../test/jwt";
import { getAccessToken } from "../../auth/tokenStore";
import { otherTabSignals } from "../../test/otherTab";
import { getTwoFactorStatus, regenerateRecoveryCodes } from "../../api/cluckwork";
import { logout, setOnTokensChanged } from "../../api/client";
import { t, freshCodes, codesDialog, renderApp, proveAndSubmit, confirmPending, regenerateOpen, leave, codesShown, frames, droppedNotice, politeRegion, tabCloseAsks, setUpRecoveryCodesApp } from "../../test/recoveryCodesApp";

// #1398 — split from recoveryCodes.test.tsx. #1277 (maintainer, 2026-11-05: "Keep codes above pages"): unsaved
// recovery codes, and the answer of a confirm or regenerate still in flight,
// live in a slot above the routes. No navigation drops them; only the end of
// the session does. Driven through a real BrowserRouter over jsdom's history,
// because a MemoryRouter would ignore Back and the skip link.
vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  startTwoFactor: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  listMyConnectedApps: vi.fn(),
}));
vi.mock("../../api/client", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/client")>(),
  stepUp: vi.fn(),
  confirmTwoFactor: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

beforeEach(setUpRecoveryCodesApp);

afterEach(() => vi.unstubAllGlobals());

// #1398 — the other tab's channel, as a tab that signs in or out posts on it.
describe("a sign-in or sign-out in another tab", () => {
  it("drops the unsaved codes, and leaves this tab's session and page alone", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    await codesShown(codes);

    await otherTabSignals();

    expect(codesDialog()).toBeNull();
    for (const code of codes) expect(document.body.innerHTML).not.toContain(code);
    expect(tabCloseAsks()).toBe(false);
    expect(screen.getByRole("heading", { name: t("twoFactorHeading") })).toBeInTheDocument();
    expect(screen.queryByText("Login page")).toBeNull();
    expect(getAccessToken()).not.toBeNull();
    expect(logout).not.toHaveBeenCalled();
  });

  // Review r2: the Security section sits low on Account, so the notice at the
  // top would be off screen. Focus goes to it once every dialog is gone, which
  // scrolls it into view (in a browser) and has a screen reader read it.
  it("moves focus to the notice once the dialogs are gone, and the offscreen region stays quiet", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await screen.findByRole("dialog", { name: t("codesHeading") });

    await otherTabSignals();

    await waitFor(() => expect(document.activeElement).toBe(droppedNotice()));
    expect(droppedNotice()).toHaveAttribute("tabindex", "-1");
    expect(politeRegion()).toHaveTextContent("");
  });

  // Review r2, finding 8: the signal can land while the step-up dialog of a
  // regenerate is still closing; focus must not fall to the page's body.
  it("still moves focus to the notice when the signal lands as the step-up dialog closes", async () => {
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: freshCodes() });
    await regenerateOpen();
    await proveAndSubmit(t("regenerateButton"), true);
    await screen.findByRole("dialog", { name: t("codesHeading"), hidden: true });

    await otherTabSignals();

    await waitFor(() => expect(document.activeElement).toBe(droppedNotice()));
    await frames(20);
    expect(document.activeElement).toBe(droppedNotice());
  });

  it("says in the shell strip that the codes were removed and Regenerate gives a new set", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    await codesShown(codes);
    expect(droppedNotice()).toBeNull();

    await otherTabSignals();

    await waitFor(() => expect(droppedNotice()).toHaveTextContent(t("codesDroppedNotice")));
    expect(droppedNotice()).toHaveTextContent(t("regenerateButton"));

    await leave("a nav click");
    expect(droppedNotice()).not.toBeNull();
  });

  it("posts no notice after Done, nor for a later signal", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });

    await otherTabSignals();

    expect(droppedNotice()).toBeNull();
  });

  // Review r1, probes P1 and P1b: with no codes held there is nothing to do,
  // so focus stays where the user is typing, however many signals arrive.
  it("leaves focus in the field the user is typing in when no codes are held", async () => {
    renderApp("/form", false);
    const notes = await screen.findByLabelText("Notes");
    notes.focus();

    await otherTabSignals();
    await otherTabSignals();
    await frames(3);

    expect(document.activeElement).toBe(notes);
    expect(droppedNotice()).toBeNull();
  });

  it("leaves focus inside an open step-up dialog when no codes are held", async () => {
    await regenerateOpen();
    const password = within(await screen.findByRole("dialog")).getByLabelText(/Your current password/);
    password.focus();

    await otherTabSignals();
    await frames(3);

    expect(document.activeElement).toBe(password);
  });

  // Probe P7: only the held set goes, never this tab's own request.
  it("leaves a code-issuing request alone, so its codes still arrive", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();

    await otherTabSignals();
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    await codesShown(codes);
  });

  // Probe P9: the listener hears every signal, not only the first.
  it("drops a second set on a second signal", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await otherTabSignals();
    expect(codesDialog()).toBeNull();

    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: freshCodes() });
    setStoredToken({ sub: "u1", role: "Worker", two_factor: "true" });
    await act(async () => { vi.mocked(setOnTokensChanged).mock.calls.at(-1)?.[0]?.(); });
    await act(async () => { fireEvent.click(await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 3000 })); });
    await proveAndSubmit(t("regenerateButton"), true);
    await screen.findByRole("dialog", { name: t("codesHeading") });

    await otherTabSignals();
    expect(codesDialog()).toBeNull();
  });

  // Probe P11: a provider that is gone no longer listens.
  // Unmounted while a set is held, so a listener left behind would still act.
  it("stops listening once the provider is gone", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await screen.findByRole("dialog", { name: t("codesHeading") });
    cleanup();
    const main = document.createElement("main");
    main.id = MAIN_CONTENT_ID;
    main.tabIndex = -1;
    const input = document.createElement("input");
    main.append(input);
    document.body.append(main);
    input.focus();

    await otherTabSignals();
    await frames(3);

    expect(document.activeElement).toBe(input);
    main.remove();
  });
});
