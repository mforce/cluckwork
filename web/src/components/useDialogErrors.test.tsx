import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { useDialogErrors } from "./useDialogErrors";

// #479 — the rules this hook owns, proven once here instead of nine times
// across the screens that use it. They were all learned the hard way on Sales
// (#474 → #477 → #480), where each was a separate review round:
//
//   • a dialog's failure renders in THAT dialog, never in another and never on
//     the page behind it;
//   • the page's failures and a dialog's cannot overwrite each other;
//   • clearing one dialog clears one dialog;
//   • an attempt clears its own slot before it starts, so a stale verdict is
//     never mistaken for the current one;
//   • a dismissed attempt's failure lands nowhere, because by the time it
//     arrives the dialog may be a SECOND session the user reopened.
function Host() {
  const errors = useDialogErrors();
  return (
    <>
      <p data-testid="page">{errors.page ?? "—"}</p>
      <p data-testid="new">{errors.forDialog("new") ?? "—"}</p>
      <p data-testid="edit">{errors.forDialog("edit") ?? "—"}</p>

      <button onClick={() => errors.setPage("page failed")}>fail page</button>
      <button onClick={() => errors.setPage(null)}>clear page</button>
      <button onClick={() => errors.report("new", "new failed")}>fail new</button>
      <button onClick={() => errors.report("edit", "edit failed")}>fail edit</button>
      <button onClick={() => errors.clearDialog("new")}>clear new</button>
      <button onClick={() => errors.beginAttempt("new")}>start new attempt</button>
      <button onClick={() => errors.beginAttempt(null)}>start page attempt</button>

      <button onClick={() => errors.abandon("new")}>dismiss new</button>
      <button onClick={() => errors.abandon("edit")}>dismiss edit</button>
      <button onClick={() => errors.report("new", "new failed late")}>report new</button>
      <button onClick={() => errors.report("edit", "edit failed late")}>report edit</button>
      <button onClick={() => errors.report(null, "page failed late")}>report page</button>
    </>
  );
}

const shown = (id: string) => screen.getByTestId(id).textContent;
const click = (name: string) => fireEvent.click(screen.getByRole("button", { name }));

describe("useDialogErrors", () => {
  it("keeps each dialog's failure to itself", () => {
    render(<Host />);
    click("fail new");

    expect(shown("new")).toBe("new failed");
    expect(shown("edit")).toBe("—");
    expect(shown("page")).toBe("—");
  });

  it("lets two dialogs hold their own message at the same time", () => {
    // The single-slot version erased the first when the second failed — a form
    // losing its explanation with nothing happening inside it (#481).
    render(<Host />);
    click("fail new");
    click("fail edit");

    expect(shown("new")).toBe("new failed");
    expect(shown("edit")).toBe("edit failed");
  });

  it("keeps the page's failure and a dialog's apart in both directions", () => {
    render(<Host />);
    click("fail page");
    click("fail new");
    expect(shown("page")).toBe("page failed");
    expect(shown("new")).toBe("new failed");

    click("fail page");
    expect(shown("new")).toBe("new failed");
  });

  it("clears one dialog without touching the other", () => {
    render(<Host />);
    click("fail new");
    click("fail edit");

    click("clear new");

    expect(shown("new")).toBe("—");
    expect(shown("edit")).toBe("edit failed");
  });

  it("clears nothing but its own slot when an attempt starts", () => {
    // A form mid-save must not still show why the PREVIOUS attempt failed, and
    // starting one attempt must not silently drop a failure the user has not
    // dealt with elsewhere.
    render(<Host />);
    click("fail page");
    click("fail new");
    click("fail edit");

    click("start new attempt");

    expect(shown("new")).toBe("—");
    expect(shown("edit")).toBe("edit failed");
    expect(shown("page")).toBe("page failed");
  });

  it("clears only the page slot when a page attempt starts", () => {
    render(<Host />);
    click("fail page");
    click("fail new");

    click("start page attempt");

    expect(shown("page")).toBe("—");
    expect(shown("new")).toBe("new failed");
  });

  it("clears the page slot on demand", () => {
    render(<Host />);
    click("fail page");
    click("clear page");
    expect(shown("page")).toBe("—");
  });

  // #474, generalised. Nothing gates a dialog trigger on `busy`, so the user
  // can dismiss a form whose write is still out and reopen it immediately.
  // That late failure has nowhere honest to land: not on the page, which is
  // the context-free message #474 was filed about, and not in the dialog,
  // which by now is a session it knows nothing about.
  describe("a dismissed attempt", () => {
    it("reports nowhere when its failure finally lands", () => {
      render(<Host />);
      click("dismiss new");

      click("report new");

      expect(shown("new")).toBe("—");
      expect(shown("page")).toBe("—");
    });

    it("leaves its slot blank, so reopening the form shows no stale verdict", () => {
      render(<Host />);
      click("fail new");

      click("dismiss new");

      expect(shown("new")).toBe("—");
    });

    it("does not mute the next attempt in the same dialog", () => {
      // The reopen case. Muting is per attempt, not per dialog — otherwise
      // the form the user is filling in now would never be able to fail.
      render(<Host />);
      click("dismiss new");

      click("start new attempt");
      click("report new");

      expect(shown("new")).toBe("new failed late");
    });

    it("mutes only itself, not another open dialog", () => {
      render(<Host />);
      click("dismiss new");

      click("report new");
      click("report edit");

      expect(shown("new")).toBe("—");
      expect(shown("edit")).toBe("edit failed late");
    });

    it("never mutes the page", () => {
      render(<Host />);
      click("dismiss new");

      click("report page");

      expect(shown("page")).toBe("page failed late");
    });

    it("does not clear another dialog's message", () => {
      render(<Host />);
      click("fail edit");

      click("dismiss new");

      expect(shown("edit")).toBe("edit failed");
    });

    it("stays muted alongside a second abandoned dialog", () => {
      // Both at once, because nothing enforces one-open-dialog (#480). The
      // mute has to be a SET that accumulates: replacing it wholesale on each
      // dismissal would un-mute whichever attempt was abandoned first, and
      // every other test here abandons only one scope, so nothing else looks.
      render(<Host />);
      click("dismiss new");
      click("dismiss edit");

      click("report new");
      click("report edit");

      expect(shown("new")).toBe("—");
      expect(shown("edit")).toBe("—");
    });

    it("is un-muted one scope at a time", () => {
      // The other half of the same rule: reopening one form must not revive
      // the other's abandoned attempt.
      render(<Host />);
      click("dismiss new");
      click("dismiss edit");

      click("start new attempt");
      click("report new");
      click("report edit");

      expect(shown("new")).toBe("new failed late");
      expect(shown("edit")).toBe("—");
    });

    it("stays muted however many times its attempt reports", () => {
      // `report` is a read, not a write. A settle path that reports twice — a
      // catch plus a finally, a retry wrapper, a validation throw after a
      // caught network error — must not have its SECOND message appear in the
      // dialog the user dismissed. Consuming the mute on first read did
      // exactly that, and is why it does not.
      render(<Host />);
      click("dismiss new");

      click("report new");
      click("report new");

      expect(shown("new")).toBe("—");
    });

    it("survives a page attempt starting", () => {
      // A page attempt owns the page's slot and nothing else. Clearing the
      // mute set here would revive an abandoned dialog attempt because the
      // user happened to do something unrelated on the screen behind.
      render(<Host />);
      click("dismiss new");

      click("start page attempt");
      click("report new");

      expect(shown("new")).toBe("—");
    });
  });

  it("routes a reported failure to the slot its scope names", () => {
    render(<Host />);
    click("report new");
    click("report page");

    expect(shown("new")).toBe("new failed late");
    expect(shown("page")).toBe("page failed late");
    expect(shown("edit")).toBe("—");
  });
});
