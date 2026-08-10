import { describe, it, expect } from "vitest";
import { render, screen, act, cleanup } from "@testing-library/react";
import { useState } from "react";
import { Dialog } from "./Dialog";
import { useLiveAnnouncement } from "./useLiveAnnouncement";

// #485 — the hook behind the announcers in UpdatePrompt and AppLayout. The
// suites there drive it through real components; this one pins the two edges
// those cannot reach.

function Probe({ message }: { message: string | null }) {
  const announcement = useLiveAnnouncement(message);
  return <span data-testid="out">{announcement}</span>;
}

const out = () => screen.getByTestId("out");

describe("useLiveAnnouncement (#485)", () => {
  it("withholds the message on its very FIRST render under an already-open dialog", async () => {
    // Deliberately not awaited past the first paint: the subscription's own
    // notification arrives a microtask later, and by then the answer is right
    // whatever the initial value was. The bug this pins is the one frame in
    // between — a hook seeded "nothing is open" hands its message straight to
    // a region that is already inert, which both spends the announcement
    // unheard AND leaves nothing to change when the dialog finally closes.
    await act(async () => {
      render(
        <Dialog open title="Blocking" onClose={() => {}}>
          <input aria-label="Field" />
        </Dialog>,
      );
    });

    // A separate mount, the way a late-mounting consumer would arrive.
    render(<Probe message="something to say" />);
    expect(out()).toHaveTextContent("");

    cleanup();
  });

  it("says nothing when there is no message, dialog or not", async () => {
    await act(async () => { render(<Probe message={null} />); });
    expect(out()).toHaveTextContent("");
  });

  it("carries the message when the page is the user's own", async () => {
    await act(async () => { render(<Probe message="ready" />); });
    expect(out()).toHaveTextContent("ready");
  });

  it("blanks and restores as a dialog opens and closes over it", async () => {
    function Harness() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <Probe message="ready" />
          <button onClick={() => setOpen(true)}>Open</button>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Field" />
          </Dialog>
        </>
      );
    }

    await act(async () => { render(<Harness />); });
    expect(out()).toHaveTextContent("ready");

    await act(async () => { screen.getByRole("button", { name: "Open" }).click(); });
    expect(out()).toHaveTextContent("");

    await act(async () => { screen.getByRole("button", { name: "Close" }).click(); });
    expect(out()).toHaveTextContent("ready");
  });
});
