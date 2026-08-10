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
//     never mistaken for the current one.
function Host() {
  const errors = useDialogErrors();
  return (
    <>
      <p data-testid="page">{errors.page ?? "—"}</p>
      <p data-testid="new">{errors.forDialog("new") ?? "—"}</p>
      <p data-testid="edit">{errors.forDialog("edit") ?? "—"}</p>

      <button onClick={() => errors.setPage("page failed")}>fail page</button>
      <button onClick={() => errors.setPage(null)}>clear page</button>
      <button onClick={() => errors.setDialog("new", "new failed")}>fail new</button>
      <button onClick={() => errors.setDialog("edit", "edit failed")}>fail edit</button>
      <button onClick={() => errors.clearDialog("new")}>clear new</button>
      <button onClick={() => errors.beginAttempt("new")}>start new attempt</button>
      <button onClick={() => errors.beginAttempt(null)}>start page attempt</button>
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
});
