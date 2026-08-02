import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { Routes, Route } from "react-router";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { login as apiLogin, ApiError } from "../api/client";
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

// #283 follow-up — the first-run notice. Driven by the SIGN-IN ATTEMPT, not by
// a status call on mount: the server reports "the default account has no
// Owner" on the 401 it already returns.
describe("Login — first-run setup notice", () => {
  const noAccounts = () =>
    new ApiError(401, "Auth.NoAccountsProvisioned", "This farm has no administrator account yet.");

  it("shows the notice, and suppresses the generic denial, when the server reports no Owner", async () => {
    mockApiLogin.mockRejectedValue(noAccounts());
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(i18n.t("auth:noAdminYet"))).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:noAdminYetHint"))).toBeInTheDocument();
    // "Invalid email or password" is the wrong thing to show the operator this
    // exists for: holding no credentials at all, they are told about a typing
    // mistake they did not make. (Not "nothing was wrong with what was typed" —
    // a seeded non-Owner CAN reach this notice by genuinely mistyping their own
    // password, which the case below pins.)
    expect(screen.queryByText(i18n.t("auth:invalidCredentials"))).not.toBeInTheDocument();
  });

  // The other side of the same boundary. Without this, a build that showed the
  // notice for EVERY 401 would pass the test above — and would tell a user who
  // simply mistyped their password that the farm has no administrator.
  it("shows the ordinary invalid-credentials message on a normal 401, with no notice", async () => {
    mockApiLogin.mockRejectedValue(new ApiError(401, "Auth.InvalidCredentials", "bad creds"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "wrong");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    expect(await screen.findByText(i18n.t("auth:invalidCredentials"))).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:noAdminYet"))).not.toBeInTheDocument();
  });

  // The status half of the guard, which nothing else covers (PR #363 review).
  // isNoAccountsProvisioned checks BOTH the title and a 401; every other
  // "no notice" case above varies the title, so deleting `&& err.status === 401`
  // left the whole suite green. A future non-401 response reusing the title —
  // a 500 from an error handler that echoes it, say — would then render a
  // first-run notice on a healthy, provisioned instance.
  it("ignores the code on a non-401 response", async () => {
    mockApiLogin.mockRejectedValue(
      new ApiError(500, "Auth.NoAccountsProvisioned", "Internal Server Error"));
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    // Falls through to the ordinary error path instead.
    expect(await screen.findByText(/Could not sign in/)).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:noAdminYet"))).not.toBeInTheDocument();
  });

  // Nothing is asked of the server on mount any more, so nothing may be shown
  // before someone actually tries. This is what pins the mechanism change: a
  // reintroduced page-load poll would surface the notice here.
  it("shows nothing before a sign-in has been attempted", async () => {
    renderWithProviders(tree(), { route: "/login", token: null });

    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:noAdminYet"))).not.toBeInTheDocument();
    expect(mockApiLogin).not.toHaveBeenCalled();
  });

  // The notice deliberately publishes NO command and no deployment detail: an
  // earlier version printed the setup invocation and was wrong twice (a form
  // that was not runnable, then two forms because the app cannot know how it
  // was deployed), and a login screen reachable by anyone is the wrong place to
  // describe how the server is run. Asserted rather than left to the copy,
  // because this is the kind of thing a well-meaning later edit re-adds.
  it("names no command and leaks no deployment detail", async () => {
    mockApiLogin.mockRejectedValue(noAccounts());
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    const notice = (await screen.findByText(i18n.t("auth:noAdminYet"))).closest(".auth-setup");
    expect(notice).not.toBeNull();
    expect(notice!.querySelector("code")).toBeNull();
    expect(notice!.textContent).not.toMatch(/docker|dotnet|bootstrap-admin|--email/i);
  });

  // The notice is an aside, not an alert: role="status" is polite, so a screen
  // reader finishes the current utterance instead of interrupting.
  it("announces the notice politely", async () => {
    mockApiLogin.mockRejectedValue(noAccounts());
    renderWithProviders(tree(), { route: "/login", token: null });

    fillCredentials("owner@farm.co", "pw");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    });

    // Located via its text, then checked for the role. Querying by role alone
    // is ambiguous here: BusyButton renders its own sr-only `role="status"`
    // span, so more than one status element is in the tree.
    const notice = await screen.findByText(i18n.t("auth:noAdminYet"));
    expect(notice.closest("[role='status']")).not.toBeNull();
  });
});
