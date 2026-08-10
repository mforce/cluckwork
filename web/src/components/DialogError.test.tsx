import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { DialogError } from "./DialogError";
import { useDialogErrors } from "./useDialogErrors";

// #479 — the render half of the split. Twenty-two of these across eleven
// screens, so the markup is decided once here rather than copied: a dialog's
// message is an alert (the user's attention is inside the form, and the text
// explains why the thing they just did did not happen), and an empty slot
// renders NOTHING rather than an empty region.
function Host() {
  const errors = useDialogErrors();
  return (
    <>
      <DialogError errors={errors} scope="new" />
      <button onClick={() => errors.setDialog("new", "new failed")}>fail new</button>
      <button onClick={() => errors.setDialog("edit", "edit failed")}>fail edit</button>
      <button onClick={() => errors.setPage("page failed")}>fail page</button>
      <button onClick={() => errors.clearDialog("new")}>clear new</button>
    </>
  );
}

const click = (name: string) => fireEvent.click(screen.getByRole("button", { name }));

describe("DialogError", () => {
  it("renders nothing while its slot is empty", () => {
    render(<Host />);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("renders its scope's message as an alert", () => {
    render(<Host />);
    click("fail new");

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toBe("new failed");
    expect(alert.className).toBe("error");
  });

  it("ignores another dialog's message", () => {
    render(<Host />);
    click("fail edit");

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("ignores the page's message", () => {
    // The whole point of the split: what the screen reported belongs to the
    // screen, and must not surface inside a form it has nothing to do with.
    render(<Host />);
    click("fail page");

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("stops rendering once its slot is cleared", () => {
    render(<Host />);
    click("fail new");
    expect(screen.getByRole("alert")).toBeTruthy();

    click("clear new");

    expect(screen.queryByRole("alert")).toBeNull();
  });
});
