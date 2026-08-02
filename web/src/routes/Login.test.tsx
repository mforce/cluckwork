import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { Routes, Route } from "react-router";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { login as apiLogin, ApiError, getProvisioningStatus } from "../api/client";
import { setStoredToken } from "../test/jwt";
import i18n from "../i18n";

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
    getProvisioningStatus: vi.fn(),
  };
});

const mockApiLogin = vi.mocked(apiLogin);
const mockProvisioningStatus = vi.mocked(getProvisioningStatus);

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
beforeEach(() => {
  vi.resetAllMocks();
  // Default every case to a provisioned instance — the state all the sign-in
  // tests below assume. resetAllMocks strips implementations, and an unstubbed
  // mock returns undefined, which Login would then call .then() on; the
  // first-run cases override this explicitly.
  mockProvisioningStatus.mockResolvedValue(true);
});

describe("Login", () => {
  it("renders its labels from the auth i18n catalog (#182)", async () => {
    renderWithProviders(tree(), { route: "/login", token: null });

    // Pinned to i18n.t, not the literal — proves the screen is reading the
    // catalog rather than a string that happens to still match it.
    expect(await screen.findByText(i18n.t("auth:title"))).toBeInTheDocument();
    expect(screen.getByLabelText(i18n.t("auth:email"))).toBeInTheDocument();
    expect(screen.getByLabelText(i18n.t("auth:password"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("auth:signIn") })).toBeInTheDocument();
  });

  it("bounces an unauthenticated visit to /login, then returns to the original route after sign-in", async () => {
    mockApiLogin.mockImplementation(async () => {
      setStoredToken({ sub: "u1", role: "Sales" }); // server issued a session (token in memory)
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

  it("redirects an ALREADY-authenticated visit AT /login away from the form (#145 silent-refresh restore)", async () => {
    // A seeded token = authenticated at mount; mounting Login itself must not
    // strand the user on the form — its effect navigates to home.
    function withHome() {
      return (
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/" element={<div>home landing</div>} />
        </Routes>
      );
    }
    renderWithProviders(withHome(), { route: "/login", token: { sub: "u1", role: "Admin" } });
    expect(await screen.findByText("home landing")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sign in" })).not.toBeInTheDocument();
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

  it("shows the server-parsed message on a 400 (oversized credential), not the generic apiDown copy (#309)", async () => {
    mockApiLogin.mockRejectedValue(
      new ApiError(400, "Bad Request", "Password must not exceed 256 characters."));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText("Password must not exceed 256 characters.")).toBeInTheDocument();
    expect(screen.queryByText(/Could not sign in/)).not.toBeInTheDocument();
  });

  it("falls back to the generic apiDown message on a 400 with no server message (#309)", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(400, "Bad Request", ""));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(/Could not sign in/)).toBeInTheDocument();
  });

  it("shows a too-long message on a 413 (oversized request body) (#309)", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(413, "Invalid request body", "too large"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(i18n.t("auth:credentialsTooLong"))).toBeInTheDocument();
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

// #283 follow-up — the first-run hint. A freshly migrated instance has base
// reference data but no users, so the form cannot succeed; without this the
// operator gets a credential prompt and no explanation.
describe("Login — first-run setup hint", () => {
  it("shows the hint, and the command to run, when the API reports no admin yet", async () => {
    mockProvisioningStatus.mockResolvedValue(false);
    renderWithProviders(tree(), { route: "/login", token: null });

    // Pinned to the catalog, not the literal, like the labels test above.
    expect(await screen.findByText(i18n.t("auth:noAdminYet"))).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:noAdminYetHint"))).toBeInTheDocument();
  });

  // The notice deliberately publishes NO command and no deployment detail: an
  // earlier version printed the setup invocation and was wrong twice (a form
  // that was not runnable, then two forms because the app cannot know how it
  // was deployed), and a login screen reachable by anyone is the wrong place to
  // describe how the server is run. Asserted rather than left to the copy,
  // because this is the kind of thing a well-meaning later edit re-adds.
  it("names no command and leaks no deployment detail", async () => {
    mockProvisioningStatus.mockResolvedValue(false);
    renderWithProviders(tree(), { route: "/login", token: null });

    const notice = (await screen.findByText(i18n.t("auth:noAdminYet"))).closest(".auth-setup");
    expect(notice).not.toBeNull();

    expect(notice!.querySelector("code")).toBeNull();
    expect(notice!.textContent).not.toMatch(/docker|dotnet|bootstrap-admin|--email/i);
  });

  it("shows nothing once an admin exists", async () => {
    mockProvisioningStatus.mockResolvedValue(true);
    renderWithProviders(tree(), { route: "/login", token: null });

    // Wait for the form so the mount effect has certainly settled — otherwise
    // this would pass simply by asserting before the answer arrived.
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:noAdminYet"))).not.toBeInTheDocument();
  });

  // The failure direction matters more than the happy path: `false` is a
  // meaningful answer, so anything that collapses an unreachable API into it
  // would tell an operator to bootstrap an instance that is already running.
  it("shows nothing when the status call fails, rather than assuming un-provisioned", async () => {
    mockProvisioningStatus.mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithProviders(tree(), { route: "/login", token: null });

    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:noAdminYet"))).not.toBeInTheDocument();
  });

  // The hint is an aside, not an alert: role="status" is polite, so a screen
  // reader finishes the current utterance instead of interrupting, and someone
  // who already has credentials is never pulled away from the form.
  it("announces the hint politely", async () => {
    mockProvisioningStatus.mockResolvedValue(false);
    renderWithProviders(tree(), { route: "/login", token: null });

    // Located via its text, then checked for the role. Querying by role alone
    // is ambiguous here: BusyButton renders its own sr-only `role="status"`
    // span, so more than one status element is in the tree. Walking ancestors
    // from the hint's own text is what ties the assertion to THIS element.
    const hint = await screen.findByText(i18n.t("auth:noAdminYet"));
    expect(hint.closest("[role='status']")).not.toBeNull();
  });

  it("never blocks sign-in while the status call is still in flight", async () => {
    // Never resolves — an instance whose status call hangs must still present a
    // fully usable form, since the hint is strictly supplementary.
    mockProvisioningStatus.mockReturnValue(new Promise<boolean>(() => {}));
    mockApiLogin.mockImplementation(async () => {
      setStoredToken({ sub: "u1", role: "Sales" });
    });
    renderWithProviders(tree(), { route: "/dashboard", token: null });

    // Await the redirect to the form first — ProtectedRoute renders nothing
    // while the load-time session restore is still settling.
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
  });
});
