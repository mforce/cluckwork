import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { StatusBadge } from "./StatusBadge";

describe("StatusBadge", () => {
  it("renders the status text verbatim so text queries still find it", () => {
    render(<StatusBadge status="Submitted" />);
    expect(screen.getByText("Submitted")).toBeInTheDocument();
  });

  it("maps known lifecycle states to their tinted variant (case-insensitive)", () => {
    const { rerender } = render(<StatusBadge status="Active" />);
    expect(screen.getByText("Active")).toHaveClass("badge", "badge-ok");

    rerender(<StatusBadge status="Voided" />);
    expect(screen.getByText("Voided")).toHaveClass("badge", "badge-danger");

    rerender(<StatusBadge status="Locked" />);
    expect(screen.getByText("Locked")).toHaveClass("badge", "badge-accent");
  });

  it("falls back to the neutral badge for an unknown status", () => {
    render(<StatusBadge status="Draft" />);
    const el = screen.getByText("Draft");
    expect(el).toHaveClass("badge");
    expect(el.className).toBe("badge"); // no variant modifier
  });

  it("uses a custom label when given, keeping the status for the variant", () => {
    render(<StatusBadge status="ManagerAdjusted" label="Adjusted" />);
    const el = screen.getByText("Adjusted");
    expect(el).toHaveClass("badge", "badge-warn");
  });
});
