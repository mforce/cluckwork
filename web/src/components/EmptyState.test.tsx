import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Inbox } from "lucide-react";
import { EmptyState } from "./EmptyState";

describe("EmptyState", () => {
  it("renders the icon and message with no button when no action is given", () => {
    const { container } = render(<EmptyState icon={Inbox} message="No flocks yet." />);
    expect(screen.getByText("No flocks yet.")).toBeInTheDocument();
    expect(container.querySelector("svg")).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("hides the icon from assistive tech (message alone carries the meaning)", () => {
    const { container } = render(<EmptyState icon={Inbox} message="No flocks yet." />);
    expect(container.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
  });

  it("renders the action button and calls the caller's own handler on click", async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();
    render(<EmptyState icon={Inbox} message="No flocks yet." action={{ label: "New flock", onClick }} />);

    const button = screen.getByRole("button", { name: "New flock" });
    await user.click(button);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("omits the button when the caller withholds action (the role-gate case)", () => {
    // A ReadOnly/Worker caller passes `action: undefined` rather than a
    // disabled button — the same shape as the page-head's own `isAdmin &&`.
    render(<EmptyState icon={Inbox} message="No flocks yet." action={undefined} />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});
