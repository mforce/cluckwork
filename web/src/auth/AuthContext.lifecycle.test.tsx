import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { AuthProvider } from "./AuthContext";
import { useAuth } from "./useAuth";
import { setStoredToken } from "../test/jwt";
import { clearTokens } from "./tokenStore";
import { login as apiLogin, logout as apiLogout, setOnTokensChanged, setOnUnauthenticated } from "../api/client";

// Mock the transport: AuthProvider drives session STATE, the client drives the
// network. We simulate the server side (login stores a token, logout clears it)
// and capture the refresh/unauth callbacks AuthProvider registers.
vi.mock("../api/client", () => ({
  login: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const mockApiLogin = vi.mocked(apiLogin);
const mockApiLogout = vi.mocked(apiLogout);
const mockSetOnTokensChanged = vi.mocked(setOnTokensChanged);
const mockSetOnUnauthenticated = vi.mocked(setOnUnauthenticated);

function Probe() {
  const { role, isAdmin, isAuthenticated, login, logout } = useAuth();
  return (
    <div>
      <span data-testid="role">{role}</span>
      <span data-testid="admin">{String(isAdmin)}</span>
      <span data-testid="auth">{String(isAuthenticated)}</span>
      <button onClick={() => void login("a@b.co", "pw")}>login</button>
      <button onClick={() => void logout()}>logout</button>
    </div>
  );
}

const renderAuth = () => render(<AuthProvider><Probe /></AuthProvider>);

beforeEach(() => {
  vi.clearAllMocks();
  clearTokens();
});

describe("AuthProvider lifecycle", () => {
  it("login derives role from the token the server stored", async () => {
    mockApiLogin.mockImplementation(async () => {
      setStoredToken({ sub: "u1", role: "Admin" }); // server issued an Admin token
      return { accessToken: "a", refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" };
    });
    renderAuth();
    expect(screen.getByTestId("auth")).toHaveTextContent("false"); // no token at mount

    await act(async () => {
      fireEvent.click(screen.getByText("login"));
    });

    expect(mockApiLogin).toHaveBeenCalledWith({ email: "a@b.co", password: "pw" });
    expect(screen.getByTestId("role")).toHaveTextContent("Admin");
    expect(screen.getByTestId("admin")).toHaveTextContent("true");
    expect(screen.getByTestId("auth")).toHaveTextContent("true");
  });

  it("logout clears auth state back to Worker", async () => {
    setStoredToken({ sub: "u1", role: "Admin" }); // start logged in
    mockApiLogout.mockImplementation(async () => clearTokens());
    renderAuth();
    expect(screen.getByTestId("admin")).toHaveTextContent("true");

    await act(async () => {
      fireEvent.click(screen.getByText("logout"));
    });

    expect(mockApiLogout).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("role")).toHaveTextContent("Worker");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
    expect(screen.getByTestId("auth")).toHaveTextContent("false");
  });

  it("re-derives the role when the token is rotated (onTokensChanged from a refresh)", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    renderAuth();
    expect(screen.getByTestId("admin")).toHaveTextContent("true");

    // AuthProvider registered a callback with the client on mount — grab it.
    const onTokensChanged = mockSetOnTokensChanged.mock.calls.at(-1)?.[0];
    expect(onTokensChanged).toBeTypeOf("function");

    // A transparent refresh rotated the token to a demoted role; the client fires
    // the callback and the UI must follow within the token lifetime.
    setStoredToken({ sub: "u1", role: "Sales" });
    act(() => onTokensChanged!());

    expect(screen.getByTestId("role")).toHaveTextContent("Sales");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
  });

  it("drops authentication when onUnauthenticated fires (refresh exhausted)", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    renderAuth();
    expect(screen.getByTestId("auth")).toHaveTextContent("true");

    const onUnauth = mockSetOnUnauthenticated.mock.calls.at(-1)?.[0];
    expect(onUnauth).toBeTypeOf("function");
    act(() => onUnauth!());

    expect(screen.getByTestId("auth")).toHaveTextContent("false");
  });
});
