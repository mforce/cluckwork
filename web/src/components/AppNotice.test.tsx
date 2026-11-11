import { describe, it, expect, vi, afterEach } from "vitest";
import { act, fireEvent, screen, within } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { setStoredToken } from "../test/jwt";
import { onTokensChanged, onUnauthenticated } from "../api/sessionState";
import { useAuth } from "../auth/useAuth";
import i18n from "../i18n";
import { PostedNotice, useAppNotice } from "./AppNotice";

// #1398 — the app's one notice: any screen or provider posts it, the shell
// strip shows it until it is closed, and it belongs to the signed-in user.
let post: (message: string) => void = () => undefined;

function Poster() {
  const { notice, show, dismiss } = useAppNotice();
  const { logout } = useAuth();
  post = show;
  return (
    <>
      <p data-testid="notice">{notice ? `${notice.severity}: ${notice.message}` : ""}</p>
      <button type="button" onClick={() => show("Codes removed.")}>post</button>
      <button type="button" onClick={() => show("Something else.", "warning")}>post another</button>
      <button type="button" onClick={dismiss}>dismiss</button>
      <button type="button" onClick={() => void logout()}>sign out</button>
      <input aria-label="Notes" />
    </>
  );
}

const notice = () => screen.getByTestId("notice").textContent;
const press = (name: string) => act(async () => { fireEvent.click(screen.getByRole("button", { name })); });
const OWNER = { sub: "u1", role: "Admin" };

afterEach(() => vi.unstubAllGlobals());

describe("the app notice", () => {
  it("holds a posted notice, info by default, until it is dismissed", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    expect(notice()).toBe("");

    await press("post");
    expect(notice()).toBe("info: Codes removed.");

    await press("dismiss");
    expect(notice()).toBe("");
  });

  it("keeps one notice at a time: a new one replaces the last", async () => {
    renderWithProviders(<Poster />, { token: OWNER });

    await press("post");
    await press("post another");

    expect(notice()).toBe("warning: Something else.");
  });

  it("shows the posted notice in the strip, as a status with a named close button, and closes by it", async () => {
    renderWithProviders(<><PostedNotice /><Poster /></>, { token: OWNER });
    const region = document.querySelector<HTMLElement>(".sr-only[aria-live]");
    expect(region).toHaveAttribute("aria-live", "polite");
    expect(region).not.toHaveAttribute("role");
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

  it("does not take focus when the strip shows a notice", async () => {
    renderWithProviders(<><PostedNotice /><Poster /></>, { token: OWNER });
    const notes = screen.getByLabelText("Notes");
    notes.focus();

    await act(async () => { post("Codes removed."); });

    expect(screen.getByRole("status")).toHaveTextContent("Codes removed.");
    expect(document.activeElement).toBe(notes);
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

  it("keeps the notice when the same user's tokens are refreshed", async () => {
    renderWithProviders(<Poster />, { token: OWNER });
    await press("post");

    setStoredToken(OWNER);
    await act(async () => { onTokensChanged?.(); });

    expect(notice()).toBe("info: Codes removed.");
  });
});
