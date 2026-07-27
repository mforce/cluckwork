import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import i18n from "../i18n";
import { BusyButton } from "./BusyButton";

describe("BusyButton", () => {
  it("idle: renders its children as the accessible name, no busy artifacts, clickable", () => {
    const onClick = vi.fn();
    render(<BusyButton onClick={onClick}>Save</BusyButton>);

    const button = screen.getByRole("button", { name: "Save" });
    expect(button).toBeEnabled();
    expect(button).not.toHaveAttribute("aria-busy");
    expect(document.querySelector(".spinner")).toBeNull();
    // The live region stays MOUNTED but empty at idle — a region that mounts
    // already populated is unreliably announced, so busy only swaps its text.
    expect(screen.getByRole("status")).toHaveTextContent("");

    fireEvent.click(button);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("busy: disables, marks aria-busy, shows an aria-hidden spinner", () => {
    render(<BusyButton busy>Save</BusyButton>);

    const button = screen.getByRole("button");
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-busy", "true");

    const spinner = document.querySelector(".spinner");
    expect(spinner).not.toBeNull();
    expect(spinner).toHaveAttribute("aria-hidden", "true");
  });

  it("busy: the accessible name is EXACTLY the children text — spinner and status must not leak in", () => {
    render(<BusyButton busy>Save</BusyButton>);
    // Exact match: existing screen tests assert names like "Sign in" verbatim,
    // so any leaked spinner/status text here breaks every one of them.
    expect(screen.getByRole("button")).toHaveAccessibleName("Save");
  });

  it("busy: announces via a status live region outside the button", () => {
    render(<BusyButton busy>Save</BusyButton>);

    const status = screen.getByRole("status");
    expect(status).toHaveTextContent("Working…");
    // Sibling, not child: aria-busy tells AT to defer announcing changes
    // INSIDE the busy element, so a region in there may never speak.
    expect(screen.getByRole("button")).not.toContainElement(status);
  });

  it("reads the announcement from the catalog, not a hardcoded literal", () => {
    const original = i18n.getResource("en", "common", "working") as string;
    i18n.addResource("en", "common", "working", "BUSY-WORKING-MARKER");
    try {
      render(<BusyButton busy>Save</BusyButton>);
      expect(screen.getByRole("status")).toHaveTextContent("BUSY-WORKING-MARKER");
    } finally {
      i18n.addResource("en", "common", "working", original);
    }
  });

  it("composes with a caller's own disabled: disabled without busy shows no busy artifacts", () => {
    render(<BusyButton disabled>Save</BusyButton>);

    const button = screen.getByRole("button", { name: "Save" });
    expect(button).toBeDisabled();
    expect(button).not.toHaveAttribute("aria-busy");
    expect(document.querySelector(".spinner")).toBeNull();
  });

  it("composes with a caller's own disabled while busy", () => {
    render(<BusyButton disabled busy>Save</BusyButton>);

    const button = screen.getByRole("button");
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-busy", "true");
  });

  it("does not fire onClick while busy", () => {
    const onClick = vi.fn();
    render(<BusyButton busy onClick={onClick}>Save</BusyButton>);

    fireEvent.click(screen.getByRole("button"));
    expect(onClick).not.toHaveBeenCalled();
  });

  it("passes button attributes through to the underlying button", () => {
    render(<BusyButton type="submit" className="btn-danger">Void</BusyButton>);

    const button = screen.getByRole("button", { name: "Void" });
    expect(button).toHaveAttribute("type", "submit");
    expect(button).toHaveClass("btn-danger");
  });
});
