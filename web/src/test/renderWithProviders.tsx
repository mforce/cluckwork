import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { render } from "@testing-library/react";
import { AuthProvider } from "../auth/AuthContext";
import { setStoredToken } from "./jwt";
import { clearAccessToken } from "../auth/tokenStore";

// Shared render harness for screen tests: wraps the UI in a MemoryRouter (so
// components using router hooks / navigation work) and the real AuthProvider (so
// role gating reflects a seeded token). Pass `token` to start authenticated as a
// given role, or omit/null for a logged-out session. Not matched by the Vitest
// `include` glob, so importing it never registers a suite.
export function renderWithProviders(
  ui: ReactNode,
  opts: { route?: string; token?: Record<string, unknown> | null } = {},
) {
  // A seeded token goes straight into memory, so AuthProvider is authenticated
  // synchronously (no load-time refresh); otherwise the session starts empty.
  if (opts.token) setStoredToken(opts.token);
  else clearAccessToken();

  return render(
    <MemoryRouter initialEntries={[opts.route ?? "/"]}>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  );
}
