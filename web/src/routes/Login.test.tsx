import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { Routes, Route } from "react-router-dom";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { login as apiLogin, ApiError } from "../api/client";
import { setStoredToken } from "../test/jwt";

// Keep the real ApiError (Login branches on `instanceof ApiError`) but stub the
// network + AuthProvider's registration hooks.
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    login: vi.fn(),
    logout: vi.fn(),
    setOnTokensChanged: vi.fn(),
    setOnUnauthenticated: vi.fn(),
  };
});

const mockApiLogin = vi.mocked(apiLogin);

// /dashboard is behind the real ProtectedRoute, so navigation there only
// succeeds if login actually established authenticated state — a bare public
// route would false-green if login stopped authenticating.
function tree() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<ProtectedRoute />}>
        <Route path="/dashboard" element={<div>dashboard (protected)</div>} />
      </Route>
    </Routes>
  );
}

function fillCredentials(email: string, password: string) {
  fireEvent.change(screen.getByLabelText(/Email/), { target: { value: email } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: password } });
}

// resetAllMocks (not clearAllMocks) so a per-test implementation never leaks
// into the next case.
beforeEach(() => vi.resetAllMocks());

describe("Login", () => {
  it("bounces an unauthenticated visit to /login, then returns to the original route after sign-in", async () => {
    mockApiLogin.mockImplementation(async () => {
      setStoredToken({ sub: "u1", role: "Sales" }); // server issued a session
      return { accessToken: "a", refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" };
    });
    // Land on the protected route while logged out → ProtectedRoute redirects to
    // /login, preserving `from = /dashboard` in router state.
    renderWithProviders(tree(), { route: "/dashboard", token: null });

    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText("dashboard (protected)")).not.toBeInTheDocument();

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(mockApiLogin).toHaveBeenCalledWith({ email: "owner@farm.co", password: "pw" });
    // Returned to the originally requested route, now authenticated.
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
  });

  it("shows an invalid-credentials message on a 401 and stays on /login", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(401, "Unauthorized", "bad creds"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "wrong");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText("Invalid email or password.")).toBeInTheDocument();
    expect(screen.queryByText("dashboard (protected)")).not.toBeInTheDocument();
  });

  it("shows a rate-limit message on a 429 and stays on /login", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(429, "Too many requests", "slow down"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(/Too many sign-in attempts/)).toBeInTheDocument();
    expect(screen.queryByText("dashboard (protected)")).not.toBeInTheDocument();
  });

  it("shows a generic error when the network fails (a non-ApiError rejection)", async () => {
    mockApiLogin.mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(/Could not sign in/)).toBeInTheDocument();
  });

  it("disables submit while a sign-in is in flight, then re-enables it on failure", async () => {
    let rejectLogin!: (err: unknown) => void;
    mockApiLogin.mockReturnValue(new Promise((_, reject) => (rejectLogin = reject)));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    // in-flight (login promise still pending): label switched and the button is
    // disabled, blocking a double submit.
    expect(screen.getByRole("button", { name: "Signing in…" })).toBeDisabled();

    await act(async () => {
      rejectLogin(new ApiError(401, "Unauthorized", "bad creds"));
    });

    // settled: re-enabled and back to the idle label
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeEnabled();
  });
});
