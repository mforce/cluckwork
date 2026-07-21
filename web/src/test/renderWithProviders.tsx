import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { render } from "@testing-library/react";
import { AuthProvider } from "../auth/AuthContext";
import { setStoredToken } from "./jwt";
import { clearTokens } from "../auth/tokenStore";

// Shared render harness for screen tests: wraps the UI in a MemoryRouter (so
// components using router hooks / navigation work) and the real AuthProvider (so
// role gating reflects a seeded token). Pass `token` to start authenticated as a
// given role, or omit/null for a logged-out session. Not matched by the Vitest
// `include` glob, so importing it never registers a suite.
export function renderWithProviders(
  ui: ReactNode,
  opts: { route?: string; token?: Record<string, unknown> | null } = {},
) {
  if (opts.token) setStoredToken(opts.token);
  else clearTokens();

  return render(
    <MemoryRouter initialEntries={[opts.route ?? "/"]}>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  );
}
