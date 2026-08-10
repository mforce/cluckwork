import { describe, it, expect } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter } from "react-router";
import { AuthProvider } from "../auth/AuthContext";
import { FarmContext } from "../farm/FarmContext";
import { farmState } from "../test/fixtures";
import { Dialog } from "../components/Dialog";
import { AppLayout } from "./AppLayout";

// #485 — same exposure as the update banner: the farm-load-failed warning
// sits inside #root, so any open Dialog makes it inert and it cannot speak
// from there. The interesting case is the one the first version of this file
// failed to set up — the read failing WHILE a dialog is already open, so the
// warning appears with the page already inert and its announcement would
// otherwise be lost for good.
//
// As in UpdatePrompt.integration.test.tsx: jsdom models no accessibility tree
// and no `inert`, so these pin the announcer's contract (empty while a dialog
// is open, populated once none is), not a real screen reader's speech.

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

const announcer = () => screen.getByRole("alert");
const visibleWarning = () => document.querySelector(".farm-warning");

describe("AppLayout farm warning announcement survives a dialog's inertness (#485)", () => {
  it("stays silent when the read fails mid-dialog, then speaks once it closes", async () => {
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

    // The read fails while the dialog is already open — a false -> true
    // transition into an inert page, which is the transition that was never
    // exercised before (codex).
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Break the farm read" }));
    });
    expect(visibleWarning()).not.toBeNull();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(/dates follow this device rather than the farm/);
  });

  it("keeps the Try again button out of the alert region", async () => {
    // An alert containing a control gets the control read out as part of the
    // warning; the visible banner keeps the button, the announcer keeps the
    // words.
    await act(async () => { render(<Shell loadFailed />); });

    expect(announcer()).toHaveTextContent(/dates follow this device rather than the farm/);
    expect(announcer().querySelector("button")).toBeNull();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });
});
