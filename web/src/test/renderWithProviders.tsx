import type { ReactNode } from "react";
import { MemoryRouter } from "react-router";
import { render } from "@testing-library/react";
import { AuthProvider } from "../auth/AuthContext";
import { FarmContext } from "../farm/FarmContext";
import type { Account } from "../api/cluckwork";
import { farmState } from "./fixtures";
import { setStoredToken } from "./jwt";
import { clearAccessToken } from "../auth/tokenStore";

// Shared render harness for screen tests: wraps the UI in a MemoryRouter (so
// components using router hooks / navigation work) and the real AuthProvider (so
// role gating reflects a seeded token). Pass `token` to start authenticated as a
// given role, or omit/null for a logged-out session. Not matched by the Vitest
// `include` glob, so importing it never registers a suite.
//
// `farm` seeds the farm context directly rather than through a FarmProvider and
// a mocked /account: a screen test cares which farm it is looking at, not how
// the shell fetched it (FarmContext.test covers that). Omitting it leaves the
// default context — no farm — which is also what the real shell shows before
// /account answers.
export function renderWithProviders(
  ui: ReactNode,
  opts: { route?: string; token?: Record<string, unknown> | null; farm?: Account } = {},
) {
  // A seeded token goes straight into memory, so AuthProvider is authenticated
  // synchronously (no load-time refresh); otherwise the session starts empty.
  if (opts.token) setStoredToken(opts.token);
  else clearAccessToken();

  const farmValue = farmState({ farm: opts.farm ?? null });

  return render(
    <MemoryRouter initialEntries={[opts.route ?? "/"]}>
      <AuthProvider>
        <FarmContext.Provider value={farmValue}>{ui}</FarmContext.Provider>
      </AuthProvider>
    </MemoryRouter>,
  );
}
