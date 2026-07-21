import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { currentUserIsAdmin } from "../auth/claims";
import { setStoredToken } from "./jwt";

// Harness smoke test: proves the jsdom + Testing Library setup can render a
// React tree, query it, and that a role-gated element reacts to the decoded
// token. It mirrors the `currentUserIsAdmin` predicate the real nav uses — it
// does not render the production nav or AuthContext (that stays the manual
// Playwright drill). localStorage is reset per test in src/test/setup.ts.
function AdminNav() {
  return (
    <nav>
      <a href="/daily">Daily</a>
      {currentUserIsAdmin() && <a href="/users">Manage users</a>}
    </nav>
  );
}

describe("role-gated nav (harness smoke)", () => {
  it("shows the admin-only link for an Admin", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    render(<AdminNav />);
    expect(screen.getByRole("link", { name: "Manage users" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Daily" })).toBeInTheDocument();
  });

  it("hides the admin-only link for a non-admin (Sales)", () => {
    setStoredToken({ sub: "u1", role: "Sales" });
    render(<AdminNav />);
    expect(screen.queryByRole("link", { name: "Manage users" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Daily" })).toBeInTheDocument();
  });
});
