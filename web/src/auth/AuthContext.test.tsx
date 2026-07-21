import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { AuthProvider } from "./AuthContext";
import { useAuth } from "./useAuth";
import { setStoredToken } from "../test/jwt";

// Exercises the REAL context wiring (AuthProvider derives its state from the
// stored token at mount) rather than the fake predicate the smoke test uses.
// localStorage is reset per test in src/test/setup.ts.
function Probe() {
  const { role, isAdmin, isAuthenticated } = useAuth();
  return (
    <div>
      <span data-testid="role">{role}</span>
      <span data-testid="admin">{String(isAdmin)}</span>
      <span data-testid="auth">{String(isAuthenticated)}</span>
    </div>
  );
}

function renderWithAuth() {
  return render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  );
}

describe("AuthProvider (real context)", () => {
  it("derives Admin + isAdmin + authenticated from an Admin token", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    renderWithAuth();
    expect(screen.getByTestId("role")).toHaveTextContent("Admin");
    expect(screen.getByTestId("admin")).toHaveTextContent("true");
    expect(screen.getByTestId("auth")).toHaveTextContent("true");
  });

  it("a Sales token is authenticated but not admin", () => {
    setStoredToken({ sub: "u1", role: "Sales" });
    renderWithAuth();
    expect(screen.getByTestId("role")).toHaveTextContent("Sales");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
    expect(screen.getByTestId("auth")).toHaveTextContent("true");
  });

  it("no token → Worker, not admin, not authenticated", () => {
    renderWithAuth();
    expect(screen.getByTestId("role")).toHaveTextContent("Worker");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
    expect(screen.getByTestId("auth")).toHaveTextContent("false");
  });
});
