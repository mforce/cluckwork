import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { AuthProvider } from "./AuthContext";
import { useAuth } from "./useAuth";
import { setStoredToken } from "../test/jwt";
import { clearAccessToken } from "./tokenStore";
import { login as apiLogin, logout as apiLogout, restoreSession, setOnTokensChanged, setOnUnauthenticated } from "../api/client";

// Mock the transport: AuthProvider drives session STATE, the client drives the
// network. We simulate the server side (login stores a token, logout clears it)
// and capture the refresh/unauth callbacks AuthProvider registers. restoreSession
// is the load-time bootstrap — default it to "no session".
vi.mock("../api/client", () => ({
  login: vi.fn(),
  logout: vi.fn(),
  restoreSession: vi.fn().mockResolvedValue(false),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const mockApiLogin = vi.mocked(apiLogin);
const mockApiLogout = vi.mocked(apiLogout);
const mockSetOnTokensChanged = vi.mocked(setOnTokensChanged);
const mockSetOnUnauthenticated = vi.mocked(setOnUnauthenticated);
const mockRestoreSession = vi.mocked(restoreSession);

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
  clearAccessToken();
});

describe("AuthProvider lifecycle", () => {
  it("awaits login, then derives role from the STORED token (not the response)", async () => {
    // Deferred so we can prove auth does not flip until login resolves, and use a
    // SALES token so the test fails if login hard-coded Admin/true instead of
    // reading storage.
    let resolveLogin!: () => void;
    mockApiLogin.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveLogin = () => {
            setStoredToken({ sub: "u1", role: "Sales" }); // server issued a Sales token
            resolve(); // login now returns void; the token comes from the store
          };
        }),
    );
    // Logged out at mount → the bootstrap silent refresh runs (mocked: no session).
    await act(async () => {
      renderAuth();
    });
    expect(screen.getByTestId("auth")).toHaveTextContent("false");

    fireEvent.click(screen.getByText("login"));
    await Promise.resolve(); // let the click's microtasks run
    expect(screen.getByTestId("auth")).toHaveTextContent("false"); // still pending → not authenticated

    await act(async () => resolveLogin());

    expect(mockApiLogin).toHaveBeenCalledWith({ email: "a@b.co", password: "pw" });
    expect(screen.getByTestId("role")).toHaveTextContent("Sales");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
    expect(screen.getByTestId("auth")).toHaveTextContent("true");
  });

  it("adopts the session the load-time silent refresh restores", async () => {
    // The success half of the #145 bootstrap. Every other case here starts
    // either already-authenticated or with restoreSession resolving false, so
    // the branch that actually ADOPTS a restored session was never executed --
    // which is what put src/auth under its coverage floor and turned main red.
    //
    // The real client stores the rotated access token before resolving, so the
    // mock does the same: the provider must derive the role from the TOKEN, not
    // from the boolean. A Manager token proves that — hard-coding Admin or
    // Worker on this path fails.
    mockRestoreSession.mockImplementationOnce(async () => {
      setStoredToken({ sub: "u1", role: "Manager" });
      return true;
    });

    await act(async () => {
      renderAuth();
    });

    expect(mockRestoreSession).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("auth")).toHaveTextContent("true");
    expect(screen.getByTestId("role")).toHaveTextContent("Manager");
    expect(screen.getByTestId("admin")).toHaveTextContent("true");
  });

  it("logout clears auth state back to Worker", async () => {
    setStoredToken({ sub: "u1", role: "Admin" }); // start logged in
    mockApiLogout.mockImplementation(async () => clearAccessToken());
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

  it("re-derives the role when the token is rotated (onTokensChanged from a refresh)", async () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    renderAuth();
    expect(screen.getByTestId("admin")).toHaveTextContent("true");

    // Exactly one registration — catches a StrictMode double-fire / missing cleanup.
    expect(mockSetOnTokensChanged).toHaveBeenCalledTimes(1);
    const onTokensChanged = mockSetOnTokensChanged.mock.calls[0][0];
    expect(onTokensChanged).toBeTypeOf("function");

    // A transparent refresh rotated the token to a demoted role; the client fires
    // the callback and the UI must follow within the token lifetime.
    setStoredToken({ sub: "u1", role: "Sales" });
    await act(async () => onTokensChanged!());

    expect(screen.getByTestId("role")).toHaveTextContent("Sales");
    expect(screen.getByTestId("admin")).toHaveTextContent("false");
  });

  it("drops authentication when onUnauthenticated fires (refresh exhausted)", async () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    renderAuth();
    expect(screen.getByTestId("auth")).toHaveTextContent("true");

    expect(mockSetOnUnauthenticated).toHaveBeenCalledTimes(1);
    const onUnauth = mockSetOnUnauthenticated.mock.calls[0][0];
    expect(onUnauth).toBeTypeOf("function");
    await act(async () => onUnauth!());

    expect(screen.getByTestId("auth")).toHaveTextContent("false");
  });

  it("unregisters its client callbacks on unmount", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    const { unmount } = renderAuth();
    mockSetOnTokensChanged.mockClear();
    mockSetOnUnauthenticated.mockClear();

    unmount();

    expect(mockSetOnTokensChanged).toHaveBeenCalledWith(null);
    expect(mockSetOnUnauthenticated).toHaveBeenCalledWith(null);
  });
});
