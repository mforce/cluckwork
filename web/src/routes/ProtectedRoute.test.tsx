import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { Routes, Route } from "react-router-dom";
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
  it("redirects to /login when there is no session", () => {
    renderWithProviders(tree(), { route: "/", token: null });
    expect(screen.getByText("login screen")).toBeInTheDocument();
    expect(screen.queryByText("protected home")).not.toBeInTheDocument();
  });

  it("renders the outlet when authenticated", () => {
    renderWithProviders(tree(), { route: "/", token: { sub: "u1", role: "Admin" } });
    expect(screen.getByText("protected home")).toBeInTheDocument();
    expect(screen.queryByText("login screen")).not.toBeInTheDocument();
  });
});
