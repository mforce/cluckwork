import { describe, it, expect, vi, beforeEach, afterEach, type MockInstance } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Link, MemoryRouter, useLocation } from "react-router";
import { ErrorBoundary } from "./ErrorBoundary";

// A child that throws during render — the exact case a boundary exists for.
function Boom({ message = "kaboom" }: { message?: string }): never {
  throw new Error(message);
}

// React logs every error a boundary catches to console.error (on top of our own
// log). Silence it so the suite output stays readable; the log-specific test
// re-reads the spy.
let errorSpy: MockInstance<typeof console.error>;
beforeEach(() => {
  errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
});
afterEach(() => {
  errorSpy.mockRestore();
  vi.unstubAllGlobals();
});

const inRouter = (ui: React.ReactNode, route = "/sales") =>
  render(<MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>);

describe("ErrorBoundary", () => {
  it("renders its children when nothing throws", () => {
    inRouter(
      <ErrorBoundary scope="screen">
        <p>all good</p>
      </ErrorBoundary>,
    );
    expect(screen.getByText("all good")).toBeInTheDocument();
    expect(screen.queryByText("Something went wrong")).not.toBeInTheDocument();
  });

  it("shows the fallback with the error text when a child throws", () => {
    inRouter(
      <ErrorBoundary scope="screen">
        <Boom message="stock blew up" />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toBeInTheDocument();
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();
    // The message is reachable (in the <details>) for a support screenshot.
    expect(screen.getByText("stock blew up")).toBeInTheDocument();
  });

  it("contains the blast radius — a sibling shell survives the child's throw", () => {
    inRouter(
      <>
        <nav>the shell</nav>
        <ErrorBoundary scope="screen">
          <Boom />
        </ErrorBoundary>
      </>,
    );
    expect(screen.getByText("the shell")).toBeInTheDocument();
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();
  });

  it("reloads the page when Reload is pressed", async () => {
    const reload = vi.fn();
    // jsdom's location.reload is non-configurable, so swap the whole location.
    // The component only reads `.reload`; MemoryRouter drives navigation itself.
    vi.stubGlobal("location", { reload });
    inRouter(
      <ErrorBoundary scope="screen">
        <Boom />
      </ErrorBoundary>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Reload" }));
    expect(reload).toHaveBeenCalledOnce();
  });

  it("recovers the screen boundary when navigation remounts it (keyed by location.key)", async () => {
    // Mirrors AppLayout's wiring: the boundary is remounted per navigation via
    // key={location.key}. The child throws as a pure function of pathname (so it
    // throws deterministically on every render at /boom, surviving React's
    // synchronous error-retry), and following the fallback's link to "/" changes
    // the key — remounting a fresh boundary — onto a route that renders.
    function Wrapper() {
      const { pathname, key } = useLocation();
      return (
        <ErrorBoundary key={key} scope="screen">
          {pathname === "/boom" ? <Boom /> : <p>recovered</p>}
        </ErrorBoundary>
      );
    }
    inRouter(<Wrapper />, "/boom");
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("link", { name: "Back to the dashboard" }));
    expect(screen.getByText("recovered")).toBeInTheDocument();
    expect(screen.queryByText("Something went wrong")).not.toBeInTheDocument();
  });

  it("catches and logs exactly once when it mounts straight into a throwing screen", () => {
    // Guards the resetKey-race that a componentDidUpdate reset would introduce:
    // navigating into a screen that throws on first render must not clear-then-
    // rethrow. The remount model has no componentDidUpdate, so the boundary
    // catches once — one log, not two.
    inRouter(
      <ErrorBoundary scope="screen">
        <Boom message="on mount" />
      </ErrorBoundary>,
    );
    expect(screen.getByText("Something went wrong")).toBeInTheDocument();
    const ourLogs = errorSpy.mock.calls.filter((c) => c[0] === "Render error caught by boundary:");
    expect(ourLogs).toHaveLength(1);
  });

  it("mints a fresh location.key on a same-path navigation (the dashboard-latch fix)", async () => {
    // Why resetKey is key, not pathname: if the dashboard ("/") is what crashed,
    // "Back to the dashboard" navigates to "/" — the same path. pathname would
    // not change and the boundary would stay latched; location.key does change,
    // so the reset fires. This pins react-router's behaviour the fix relies on.
    const keys: string[] = [];
    function Probe() {
      keys.push(useLocation().key);
      return <Link to="/">rego</Link>;
    }
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Probe />
      </MemoryRouter>,
    );
    await userEvent.click(screen.getByRole("link", { name: "rego" }));
    expect(keys.at(-1)).not.toBe(keys[0]);
  });

  it("recovers the screen boundary via a router Link, but needs no router for the app boundary", () => {
    const { unmount } = inRouter(
      <ErrorBoundary scope="screen">
        <Boom />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("link", { name: "Back to the dashboard" })).toHaveAttribute("href", "/");
    unmount();

    // No MemoryRouter here on purpose: the app boundary sits ABOVE the router, so
    // its fallback must not depend on it. It uses a plain anchor — rendering a
    // <Link> without a router context would throw, so this render passing proves
    // the scope distinction, not just a matching href.
    render(
      <ErrorBoundary scope="app">
        <Boom />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("link", { name: "Back to the dashboard" })).toHaveAttribute("href", "/");
  });

  it("logs the caught error for a support trail", () => {
    inRouter(
      <ErrorBoundary scope="screen">
        <Boom message="diagnose me" />
      </ErrorBoundary>,
    );
    expect(errorSpy).toHaveBeenCalledWith(
      "Render error caught by boundary:",
      expect.objectContaining({ message: "diagnose me" }),
      expect.anything(),
    );
  });
});
