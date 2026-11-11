import { useState } from "react";
import { describe, it, expect, vi, afterEach } from "vitest";
import { act, fireEvent, screen, within } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { setStoredToken } from "../test/jwt";
import { onTokensChanged, onUnauthenticated } from "../api/sessionState";
import { useAuth } from "../auth/useAuth";
import i18n from "../i18n";
import { MAIN_CONTENT_ID } from "../lib/mainContent";
import { PostedNotice, useAppNotice } from "./AppNotice";

// #1398 — the app's one notice: any screen or provider posts it, the shell
// strip shows it until it is closed, and it belongs to the signed-in user.
let post: (message: string) => void = () => undefined;

// The shell's <main> around a strip that can mount later, as AppLayout does
// after the forced setup screen.
function Poster({ strip = true }: { strip?: boolean }) {
  const { notice, show, dismiss } = useAppNotice();
  const { logout } = useAuth();
  const [shell, setShell] = useState(strip);
  post = show;
  return (
    <main id={MAIN_CONTENT_ID} tabIndex={-1}>
      {shell && <PostedNotice />}
      <p data-testid="notice">{notice ?? ""}</p>
      <button type="button" onClick={() => show("Codes removed.")}>post</button>
      <button type="button" onClick={() => show("Something else.")}>post another</button>
      <button type="button" onClick={dismiss}>dismiss</button>
      <button type="button" onClick={() => void logout()}>sign out</button>
      <button type="button" onClick={() => setShell(true)}>enter the shell</button>
      <input aria-label="Notes" />
    </main>
  );
}

const notice = () => screen.getByTestId("notice").textContent;
const press = (name: string) => act(async () => { fireEvent.click(screen.getByRole("button", { name })); });
const region = () => document.querySelector<HTMLElement>(".sr-only[aria-live]");
const OWNER = { sub: "u1", role: "Admin", credential_epoch: "1" };

afterEach(() => vi.unstubAllGlobals());

describe("the app notice", () => {
  it("holds a posted notice until it is dismissed", async () => {
    renderWithProviders(<Poster strip={false} />, { token: OWNER });
    expect(notice()).toBe("");

    await press("post");
    expect(notice()).toBe("Codes removed.");

    await press("dismiss");
    expect(notice()).toBe("");
  });

  it("keeps one notice at a time: a new one replaces the last", async () => {
    renderWithProviders(<Poster strip={false} />, { token: OWNER });

    await press("post");
    await press("post another");

    expect(notice()).toBe("Something else.");
  });

  it("shows the posted notice in the strip, as a status with a named close button, and closes by it", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    expect(region()).toHaveAttribute("aria-live", "polite");
    expect(region()).not.toHaveAttribute("role");
    expect(screen.queryByRole("status")).not.toBeInTheDocument();

    await press("post");
    const strip = screen.getByRole("status");
    expect(strip).toHaveTextContent("Codes removed.");
    expect(strip).toHaveClass("shell-notice");

    // The close button's name comes from the catalog, not MUI's English default.
    const close = i18n.getResource("en", "common", "close") as string;
    i18n.addResource("en", "common", "close", "CLOSE-MARKER");
    try {
      await press("post another");
      await press("post");
      await act(async () => { fireEvent.click(within(screen.getByRole("status")).getByRole("button", { name: "CLOSE-MARKER" })); });
    } finally {
      i18n.addResource("en", "common", "close", close);
    }
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(notice()).toBe("");
  });

  // Review r2, probe R3: the close button goes with the strip, so focus goes
  // to the content rather than to the page's body.
  it("puts focus on the main content when the notice is closed", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");
    const close = within(screen.getByRole("status")).getByRole("button");
    close.focus();

    await act(async () => { fireEvent.click(close); });

    expect(document.activeElement).toBe(document.getElementById(MAIN_CONTENT_ID));
  });

  it("does not take focus when the strip shows a notice", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    const notes = screen.getByLabelText("Notes");
    notes.focus();

    await act(async () => { post("Codes removed."); });

    expect(screen.getByRole("status")).toHaveTextContent("Codes removed.");
    expect(document.activeElement).toBe(notes);
  });

  // Review r2, probe R2: posted where there is no strip (the forced setup
  // screen), the notice is said once the strip mounts with it.
  it("says a notice in its offscreen region when the strip mounts with it already posted", async () => {
    renderWithProviders(<Poster strip={false} />, { token: OWNER });
    await press("post");
    expect(region()).toBeNull();

    await press("enter the shell");

    expect(screen.getByRole("status")).toHaveTextContent("Codes removed.");
    await vi.waitFor(() => expect(region()).toHaveTextContent("Codes removed."));
  });

  it("clears when this tab signs out", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");

    await press("sign out");

    expect(notice()).toBe("");
  });

  it("clears when the server ends this tab's session", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");

    await act(async () => { onUnauthenticated?.("Auth.SessionChanged"); });

    expect(notice()).toBe("");
  });

  it("clears when another user's tokens arrive", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");

    setStoredToken({ sub: "u2", role: "Worker" });
    await act(async () => { onTokensChanged?.(); });

    expect(notice()).toBe("");
  });

  // Review r2, probe R1: an enrolment confirm or a password change hands the
  // same user new credentials; the notice is still theirs.
  it("keeps the notice when the same user's tokens or credentials change", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");

    setStoredToken(OWNER);
    await act(async () => { onTokensChanged?.(); });
    expect(notice()).toBe("Codes removed.");

    setStoredToken({ ...OWNER, credential_epoch: "2", two_factor: "true" });
    await act(async () => { onTokensChanged?.(); });
    expect(notice()).toBe("Codes removed.");
  });
});
