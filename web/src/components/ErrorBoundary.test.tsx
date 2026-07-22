import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { ErrorBoundary } from "./ErrorBoundary";

// A child that throws during render — the exact case a boundary exists for.
function Boom({ message = "kaboom" }: { message?: string }): never {
  throw new Error(message);
}

// React logs every error a boundary catches to console.error (on top of our own
// log). Silence it so the suite output stays readable; the log-specific test
// re-reads the spy.
let errorSpy: ReturnType<typeof vi.spyOn>;
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

  it("recovers the screen boundary on navigation (resetKey change)", async () => {
    // Mirrors AppLayout's wiring: the pathname is the resetKey, so following the
    // fallback's "Back to the dashboard" link clears the error without a reload.
    function Wrapper() {
      const { pathname } = useLocation();
      return (
        <ErrorBoundary scope="screen" resetKey={pathname}>
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

  it("offers a link back to the dashboard in both scopes", () => {
    const { unmount } = inRouter(
      <ErrorBoundary scope="screen">
        <Boom />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("link", { name: "Back to the dashboard" })).toHaveAttribute("href", "/");
    unmount();

    inRouter(
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
