import { Component, useEffect, useRef, type ErrorInfo, type ReactNode } from "react";
import { Link } from "react-router";

// #140: without a boundary, any throw during render unmounts the whole tree and
// the user is left on a blank page — no message, no way back. On a phone in a
// barn that is indistinguishable from a dead network or a flat battery, and the
// day's count goes unrecorded. This degrades a render-time crash to a screen the
// user can read and act on.
//
// React error boundaries catch throws in render, lifecycle methods and
// constructors ONLY. They do NOT catch event-handler or async failures
// (setTimeout, promise rejections, fetch callbacks) — those already surface
// through each screen's own `error` state, which is the right place for them.
// A boundary must be a class; hooks cannot express getDerivedStateFromError.

type Scope = "screen" | "app";

type Props = {
  children: ReactNode;
  // "screen": the nav shell is still on screen, only the routed pane failed.
  // "app": the shell itself threw, so the fallback is the whole page.
  scope: Scope;
};

type State = { error: Error | null };

// Recovery is by REMOUNT, not by an internal reset prop: the screen boundary is
// mounted with `key={location.key}` (see AppLayout), so every navigation —
// including the fallback's "Back to the dashboard" link, and a same-path retry
// when the dashboard itself crashed — gives a fresh instance with no error. A
// resetKey-diffing componentDidUpdate would double-catch when you navigate
// straight into a screen that throws (the prop change and the freshly-caught
// error land in one commit), so we deliberately don't have one.
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Reachable in the console and logs for a support screenshot, without
    // shouting the stack at the user on the page.
    console.error("Render error caught by boundary:", error, info.componentStack);
  }

  render() {
    if (this.state.error) return <ErrorFallback error={this.state.error} scope={this.props.scope} />;
    return this.props.children;
  }
}

function ErrorFallback({ error, scope }: { error: Error; scope: Scope }) {
  // Move focus to the fallback when it appears. React unmounts the crashed
  // subtree, so a keyboard user's focus would otherwise be stranded on a
  // now-gone element; role="alert" announces it, and this lands focus on it too.
  const ref = useRef<HTMLElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, []);

  return (
    <section className="crash" role="alert" tabIndex={-1} ref={ref}>
      <h2>Something went wrong</h2>
      <p className="muted">
        {scope === "screen"
          ? "This screen ran into a problem and couldn’t finish loading. Anything you’d already saved is safe, but anything you were still typing here may need to be entered again. The rest of the app still works."
          : "The app ran into a problem and couldn’t finish loading. Reloading usually clears it."}
      </p>
      <div className="crash-actions">
        <button onClick={() => window.location.reload()}>Reload</button>
        {/* The screen boundary lives inside the router, so a client-side Link
            recovers it (the pathname change resets the boundary). The app
            boundary may be sitting over a broken shell, so it takes a full
            document load back to a clean slate. */}
        {scope === "screen" ? (
          <Link className="crash-home" to="/">Back to the dashboard</Link>
        ) : (
          <a className="crash-home" href="/">Back to the dashboard</a>
        )}
      </div>
      <details className="crash-detail">
        <summary>Error details</summary>
        <pre>{error.message}</pre>
      </details>
    </section>
  );
}
