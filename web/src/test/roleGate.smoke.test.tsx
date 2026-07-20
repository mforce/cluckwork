import { describe, it, expect, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { currentUserIsAdmin } from "../auth/claims";

// Harness smoke test: proves the jsdom + Testing Library setup can render a
// React tree, query it, and that a role-gated element reacts to the decoded
// token — the same isAdmin gate the real nav uses (AuthContext). Not a test of
// production markup; the app's nav wiring is exercised via the manual Playwright
// drill until a fuller component suite lands.
function AdminNav() {
  return (
    <nav>
      <a href="/daily">Daily</a>
      {currentUserIsAdmin() && <a href="/users">Manage users</a>}
    </nav>
  );
}

function setRole(role: string): void {
  const b64url = (o: unknown) =>
    btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  const accessToken = `${b64url({ alg: "HS256" })}.${b64url({ sub: "u1", role })}.sig`;
  localStorage.setItem("cluckwork.tokens", JSON.stringify({ accessToken, refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" }));
}

beforeEach(() => localStorage.clear());

describe("role-gated nav (harness smoke)", () => {
  it("shows the admin-only link for an Admin", () => {
    setRole("Admin");
    render(<AdminNav />);
    expect(screen.getByRole("link", { name: "Manage users" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Daily" })).toBeInTheDocument();
  });

  it("hides the admin-only link for a non-admin (Sales)", () => {
    setRole("Sales");
    render(<AdminNav />);
    expect(screen.queryByRole("link", { name: "Manage users" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Daily" })).toBeInTheDocument();
  });
});
