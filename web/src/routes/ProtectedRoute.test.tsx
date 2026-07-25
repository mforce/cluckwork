import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { Routes, Route } from "react-router";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";

// A minimal route tree: "/" is behind the gate, "/login" is the public landing.
function tree() {
  return (
    <Routes>
      <Route path="/login" element={<div>login screen</div>} />
      <Route element={<ProtectedRoute />}>
        <Route path="/" element={<div>protected home</div>} />
      </Route>
    </Routes>
  );
}

describe("ProtectedRoute", () => {
  it("redirects to /login when there is no session", async () => {
    // No in-memory token → the load-time silent refresh runs first (the route
    // holds, rendering nothing); when it fails (no cookie) the gate redirects.
    renderWithProviders(tree(), { route: "/", token: null });
    expect(await screen.findByText("login screen")).toBeInTheDocument();
    expect(screen.queryByText("protected home")).not.toBeInTheDocument();
  });

  it("renders the outlet when authenticated", () => {
    renderWithProviders(tree(), { route: "/", token: { sub: "u1", role: "Admin" } });
    expect(screen.getByText("protected home")).toBeInTheDocument();
    expect(screen.queryByText("login screen")).not.toBeInTheDocument();
  });
});
