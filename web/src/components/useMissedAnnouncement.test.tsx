import { describe, it, expect } from "vitest";
import { render, screen, act, cleanup } from "@testing-library/react";
import { useState } from "react";
import { Dialog } from "./Dialog";
import { useMissedAnnouncement } from "./useMissedAnnouncement";

// #485 — the hook behind the offscreen regions in UpdatePrompt and AppLayout.
// Its whole job is the announcement a VISIBLE live region could not make
// because a dialog had it inert; on every other path it must stay quiet, or
// the same sentence gets said twice.

function Probe({ message }: { message: string | null }) {
  const missed = useMissedAnnouncement(message);
  return <span data-testid="out">{missed}</span>;
}

const out = () => screen.getByTestId("out");

describe("useMissedAnnouncement (#485)", () => {
  it("stays empty on the ordinary path, where the visible region speaks for itself", async () => {
    await act(async () => { render(<Probe message="ready" />); });
    expect(out()).toHaveTextContent("");
  });

  it("is never populated on its first render, even with a message and no dialog", async () => {
    // A live region inserted with its text already inside is not reliably
    // announced (W3C ARIA22; Safari/VoiceOver drops it), so this must never
    // return a message before the element has had a render to exist in. Not
    // hypothetical: SessionProvider gates the shell on /account settling, so a
    // failed read mounts AppLayout with its warning already true (codex review
    // of #499). Deliberately NOT awaited past the first paint — the point is
    // what the very first render produced.
    render(<Probe message="something to say" />);
    expect(out()).toHaveTextContent("");
    cleanup();
  });

  it("records the debt when it MOUNTS under a dialog that is already open", async () => {
    // Two separate mounts on purpose. Within one tree the dialog pushes onto
    // the stack from an effect, which runs after this hook's first render, so
    // the stack is legitimately empty at that point. Mounting afterwards is
    // the case where reading the live stack matters: seeded "nothing is open",
    // the hook records no debt, and the correction that arrives a microtask
    // later is too late — the message has stopped changing by then, so the
    // announcement is dropped and nothing is ever said.
    function DialogHost() {
      const [open, setOpen] = useState(true);
      return (
        <Dialog open={open} title="Blocking" onClose={() => setOpen(false)}>
          <input aria-label="Field" />
        </Dialog>
      );
    }

    await act(async () => { render(<DialogHost />); });
    await act(async () => { render(<Probe message="ready" />); });
    expect(out()).toHaveTextContent("");

    await act(async () => { screen.getByRole("button", { name: "Close" }).click(); });
    expect(out()).toHaveTextContent("ready");
  });

  it("delivers a message that arrived while a dialog was open, once it closes", async () => {
    function Harness() {
      const [open, setOpen] = useState(true);
      const [msg, setMsg] = useState<string | null>(null);
      return (
        <>
          <Probe message={msg} />
          <button onClick={() => setMsg("ready")}>Raise</button>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Field" />
          </Dialog>
        </>
      );
    }

    await act(async () => { render(<Harness />); });
    await act(async () => { screen.getByRole("button", { name: "Raise" }).click(); });
    // Inert: the visible region's own announcement went unheard, and writing
    // here now would go unheard too.
    expect(out()).toHaveTextContent("");

    await act(async () => { screen.getByRole("button", { name: "Close" }).click(); });
    expect(out()).toHaveTextContent("ready");
  });

  it("does not repeat itself when a later dialog opens and closes over the same message", async () => {
    // Once delivered the text stays put rather than being blanked, so the next
    // dialog cycle produces no change and therefore no second announcement.
    // Blanking on every open would turn a standing banner into a nag.
    function Harness() {
      const [open, setOpen] = useState(true);
      const [msg, setMsg] = useState<string | null>(null);
      return (
        <>
          <Probe message={msg} />
          <button onClick={() => setMsg("ready")}>Raise</button>
          <button onClick={() => setOpen(true)}>Open</button>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Field" />
          </Dialog>
        </>
      );
    }

    await act(async () => { render(<Harness />); });
    await act(async () => { screen.getByRole("button", { name: "Raise" }).click(); });
    await act(async () => { screen.getByRole("button", { name: "Close" }).click(); });
    expect(out()).toHaveTextContent("ready");

    await act(async () => { screen.getByRole("button", { name: "Open" }).click(); });
    // Still holding the delivered text — unheard behind the dialog, and
    // crucially unchanged, so closing again says nothing new.
    expect(out()).toHaveTextContent("ready");
    await act(async () => { screen.getByRole("button", { name: "Close" }).click(); });
    expect(out()).toHaveTextContent("ready");
  });

  it("clears once there is nothing left to say", async () => {
    function Harness() {
      const [msg, setMsg] = useState<string | null>("ready");
      return (
        <>
          <Probe message={msg} />
          <button onClick={() => setMsg(null)}>Resolve</button>
        </>
      );
    }

    await act(async () => { render(<Harness />); });
    await act(async () => { screen.getByRole("button", { name: "Resolve" }).click(); });
    expect(out()).toHaveTextContent("");
  });
});
