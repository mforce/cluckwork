import { describe, it, expect } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { HelpPage } from "./HelpPage";

describe("HelpPage", () => {
  it("renders the guide with a contents rail linking to its sections", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Help", level: 2 })).toBeInTheDocument();

    // the contents rail links to a section anchor
    const toc = screen.getByRole("navigation", { name: "Help contents" });
    expect(within(toc).getByRole("link", { name: "The daily loop" })).toHaveAttribute("href", "#daily-loop");

    // the section it points at, and a glossary definition, are present
    expect(screen.getByRole("heading", { name: "The daily loop", level: 3 })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "FIFO" })).toBeInTheDocument();
  });
});
