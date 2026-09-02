import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { GlossaryLink } from "./GlossaryLink";

// #657 — a "?" beside a column header that carries a glossary term, linking
// to that term's anchor on the Help page.
describe("GlossaryLink", () => {
  it("links to the term's anchor on the Help page and names the term for assistive tech", () => {
    render(<MemoryRouter><GlossaryLink term="WithdrawalRestriction" /></MemoryRouter>);
    const link = screen.getByRole("link", { name: "What does “Withdrawal restriction” mean?" });
    expect(link).toHaveAttribute("href", "/help#glossary-withdrawal-restriction");
    expect(link).toHaveTextContent("?");
  });
});
