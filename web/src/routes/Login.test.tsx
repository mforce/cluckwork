import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { Routes, Route } from "react-router-dom";
import { Login } from "./Login";
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

function tree() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/" element={<div>home dashboard</div>} />
    </Routes>
  );
}

function fillCredentials(email: string, password: string) {
  fireEvent.change(screen.getByLabelText(/Email/), { target: { value: email } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: password } });
}

beforeEach(() => vi.clearAllMocks());

describe("Login", () => {
  it("signs in and navigates to the landing route on success", async () => {
    mockApiLogin.mockImplementation(async () => {
      setStoredToken({ sub: "u1", role: "Admin" }); // server issued a session
      return { accessToken: "a", refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" };
    });
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(mockApiLogin).toHaveBeenCalledWith({ email: "owner@farm.co", password: "pw" });
    expect(await screen.findByText("home dashboard")).toBeInTheDocument(); // navigated away
  });

  it("shows an invalid-credentials message on a 401", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(401, "Unauthorized", "bad creds"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "wrong");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText("Invalid email or password.")).toBeInTheDocument();
    expect(screen.queryByText("home dashboard")).not.toBeInTheDocument(); // stayed on /login
  });

  it("shows a generic error when the API is unreachable (non-401)", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(500, "Server error", "down"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(/Could not sign in/)).toBeInTheDocument();
  });
});
