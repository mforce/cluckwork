import { describe, it, expect, vi } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { useState } from "react";
import { Dialog } from "../components/Dialog";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";

// #485 — #483 makes everything outside the topmost dialog `inert`, which takes
// the update banner out of the accessibility tree. A banner that appears while
// a dialog is open is therefore never announced, and un-inerting it afterwards
// replays nothing. An offscreen live region covers exactly that gap.
//
// What these tests can and cannot prove, stated plainly because an earlier
// version of this file got it wrong: jsdom implements no accessibility tree
// and (as of 29.1.1) not even the `inert` IDL, so NOTHING here observes a real
// announcement. What they pin is the contract underneath it — who holds the
// message, and when. Whether a screen reader then voices that mutation is a
// browser/AT matter for manual verification.

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

// Role-less on purpose (see UpdatePrompt.tsx): it is always mounted, and a
// permanent `role="status"` would answer every such query in the app.
function announcer(): HTMLElement {
  const el = document.querySelector<HTMLElement>(".sr-only[aria-live]");
  if (el === null) throw new Error("no offscreen live region rendered");
  return el;
}
const visibleBanner = () => document.querySelector(".update-banner");
const MESSAGE = "A new version of Cluckwork is ready";

async function renderWithUpdate(ui: React.ReactElement) {
  let announce: ((activate: () => Promise<void>) => void) | undefined;
  mockRegister.mockImplementation(async (onUpdate) => {
    announce = onUpdate;
    return null;
  });
  await act(async () => { render(ui); });
  return {
    announce: async () => {
      await act(async () => { announce?.(vi.fn().mockResolvedValue(undefined)); });
    },
  };
}

function withDialog(initiallyOpen: boolean) {
  return function Harness() {
    const [open, setOpen] = useState(initiallyOpen);
    return (
      <>
        <UpdatePrompt />
        <button onClick={() => setOpen(true)}>Open</button>
        <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
          <input aria-label="Name" />
        </Dialog>
      </>
    );
  };
}

describe("UpdatePrompt announcement survives a dialog's inertness (#485)", () => {
  it("leaves the ordinary path to the visible banner, saying nothing itself", async () => {
    // No dialog: the banner is a live region in the accessibility tree and
    // announces its own arrival. A second region holding the same sentence
    // would have a screen reader say it twice.
    const Harness = withDialog(false);
    const { announce } = await renderWithUpdate(<Harness />);
    await announce();

    expect(visibleBanner()).not.toBeNull();
    expect(screen.getByRole("status")).toHaveTextContent(MESSAGE);
    expect(announcer()).toHaveTextContent("");
  });

  it("covers the banner that appeared while a dialog was open, once it closes", async () => {
    const Harness = withDialog(true);
    const { announce } = await renderWithUpdate(<Harness />);
    // The update lands with the dialog ALREADY open, so the banner mounts
    // straight into an inert subtree and its own announcement is lost.
    await announce();

    expect(visibleBanner()).not.toBeNull();
    // Writing here now would be spent on a moment nobody hears, and would
    // leave nothing to change on the way out.
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);
  });

  it("waits for the LAST dialog, not the first", async () => {
    function TwoDialogs() {
      const [a, setA] = useState(true);
      const [b, setB] = useState(true);
      return (
        <>
          <UpdatePrompt />
          <Dialog open={a} title="A" onClose={() => setA(false)}>
            <input aria-label="A field" />
          </Dialog>
          <Dialog open={b} title="B" onClose={() => setB(false)}>
            <input aria-label="B field" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<TwoDialogs />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    // One down, one still open — the page is still inert, so still silence.
    await act(async () => {
      fireEvent.click(screen.getAllByRole("button", { name: "Close" })[0]);
    });
    expect(announcer()).toHaveTextContent("");

    // The last one closes: now it speaks. Asserting this positive half is what
    // stops an implementation that never fires at all from passing.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);
  });

  it("does not nag: a later dialog cycle over the same banner says nothing new", async () => {
    const Harness = withDialog(true);
    const { announce } = await renderWithUpdate(<Harness />);
    await announce();
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);

    // The banner is standing, unchanged. Blanking the region on every dialog
    // open would make the next close re-announce it, over and over.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Open" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);
  });

  it("stays silent when one dialog is swapped for another in a single commit", async () => {
    // The stack is momentarily empty between the outgoing dialog's effect
    // cleanup and the incoming one's setup, but the page never becomes the
    // user's own — it is inert before and after.
    function Swap() {
      const [which, setWhich] = useState<"a" | "b">("a");
      return (
        <>
          <UpdatePrompt />
          <button onClick={() => setWhich("b")}>Swap</button>
          <Dialog open={which === "a"} title="A" onClose={() => {}}>
            <input aria-label="A field" />
          </Dialog>
          <Dialog open={which === "b"} title="B" onClose={() => {}}>
            <input aria-label="B field" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<Swap />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Swap" }));
    });

    expect(screen.getByRole("dialog", { name: "B" })).toBeInTheDocument();
    expect(announcer()).toHaveTextContent("");
  });

  it("still covers a prompt mounted BELOW the dialog in the tree", async () => {
    // Ordering, not cosmetics: Dialog pushes onto the stack from its own
    // effect, and effects run in tree order, so a prompt rendered after it has
    // not subscribed yet at that moment. Told inline, nobody would be
    // listening, and it would keep the "no dialog open" reading it took before
    // the dialog existed — never registering that it owed an announcement.
    function DialogFirst() {
      const [open, setOpen] = useState(true);
      return (
        <>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Name" />
          </Dialog>
          <UpdatePrompt />
        </>
      );
    }

    const { announce } = await renderWithUpdate(<DialogFirst />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent(MESSAGE);
  });

  it("claims no live ARIA role of its own", async () => {
    // The regression this pins actually shipped to CI: the region first went
    // out as `role="status"`, and because `.sr-only` is a 1px box rather than
    // `display:none` a browser counts it VISIBLE. Ten Playwright specs read
    // `getByRole("alert")` being hidden as "nothing has gone wrong on this
    // screen"; a permanently-mounted node holding a live role answers all of
    // them and turns the check into a tautology.
    const { announce } = await renderWithUpdate(<UpdatePrompt />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    await announce();
    // The VISIBLE banner is the role="status" here, as it always was...
    expect(screen.getByRole("status")).toHaveClass("update-banner");
    // ...and the offscreen region still claims nothing.
    expect(announcer()).not.toHaveAttribute("role");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("says nothing when there is no update, dialog or not", async () => {
    const Harness = withDialog(true);
    await renderWithUpdate(<Harness />);
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    // Closing a dialog is not itself news.
    expect(announcer()).toHaveTextContent("");
    expect(visibleBanner()).toBeNull();
  });
});
