import { describe, it, expect } from "vitest";
import { render, screen, act, fireEvent, within } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter } from "react-router";
import { AuthProvider } from "../auth/AuthContext";
import { FarmContext } from "../farm/FarmContext";
import { farmState } from "../test/fixtures";
import { Dialog } from "../components/Dialog";
import { AppLayout } from "./AppLayout";

// #485 — same exposure as the update banner: the farm-load-failed warning sits
// inside #root, so any open Dialog makes it inert and its own role="alert"
// cannot speak from there. The case that matters, and the one an earlier
// version of this file failed to set up, is the read failing WHILE a dialog is
// open — the warning then appears into an already-inert page and its
// announcement is lost for good.
//
// As in UpdatePrompt.integration.test.tsx: jsdom models neither the
// accessibility tree nor `inert`, so these pin who holds the message and when,
// not a screen reader's speech.

function Shell({ loadFailed, children }: { loadFailed: boolean; children?: React.ReactNode }) {
  return (
    <MemoryRouter>
      <AuthProvider>
        <FarmContext.Provider value={farmState({
          farm: null, loadFailed, refresh: async () => true,
        })}>
          <AppLayout />
          {children}
        </FarmContext.Provider>
      </AuthProvider>
    </MemoryRouter>
  );
}

// Role-less on purpose (see AppLayout.tsx): always mounted, so a `role="alert"`
// here would answer every "is anything wrong on screen" query in the suite.
function announcer(): HTMLElement {
  const el = document.querySelector<HTMLElement>(".sr-only[aria-live]");
  if (el === null) throw new Error("no offscreen live region rendered");
  return el;
}
const visibleWarning = () => document.querySelector<HTMLElement>(".farm-warning");
const NEVER_LOADED = /dates follow this device rather than the farm/;

describe("AppLayout farm warning announcement survives a dialog's inertness (#485)", () => {
  it("leaves the ordinary path to the visible banner, saying nothing itself", async () => {
    // No dialog: the banner is a role="alert" in the accessibility tree and
    // announces itself, exactly as it did before #485.
    await act(async () => { render(<Shell loadFailed />); });

    expect(screen.getByRole("alert")).toHaveTextContent(NEVER_LOADED);
    expect(screen.getByRole("alert")).toHaveClass("farm-warning");
    expect(announcer()).toHaveTextContent("");
  });

  it("covers a read that fails mid-dialog, once the dialog closes", async () => {
    function Harness() {
      const [open, setOpen] = useState(true);
      const [failed, setFailed] = useState(false);
      return (
        <>
          <Shell loadFailed={failed}>
            <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
              <input aria-label="Name" />
            </Dialog>
          </Shell>
          <button onClick={() => setFailed(true)}>Break the farm read</button>
        </>
      );
    }

    await act(async () => { render(<Harness />); });
    expect(visibleWarning()).toBeNull();

    // false -> true into an already-inert page: the transition the visible
    // role="alert" cannot announce, and the one never exercised before.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Break the farm read" }));
    });
    expect(visibleWarning()).not.toBeNull();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(NEVER_LOADED);
  });

  it("keeps the warning inside the app's alert vocabulary", async () => {
    // The E2E suite reads `getByRole("alert")` being hidden as "nothing has
    // gone wrong on this screen"; the farm warning has to stay inside that net
    // or a failed /account passes those checks vacuously while every date
    // field silently follows the device clock (codex review of #499).
    await act(async () => { render(<Shell loadFailed />); });

    const banner = screen.getByRole("alert");
    expect(banner).toHaveClass("farm-warning");
    expect(within(banner).getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("exposes no alert or status role on a healthy screen", async () => {
    // The offscreen region is permanently mounted, and `.sr-only` is a 1px box
    // rather than `display:none`, so a browser counts it visible. Holding a
    // live ROLE there would answer the query above on every healthy screen.
    await act(async () => { render(<Shell loadFailed={false} />); });

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    // ...while the region is still present, and still able to speak.
    expect(announcer()).toBeInTheDocument();
    expect(announcer()).not.toHaveAttribute("role");
    expect(announcer()).toHaveAttribute("aria-live", "assertive");
  });
});
