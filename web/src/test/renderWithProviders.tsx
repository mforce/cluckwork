import { useState } from "react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router";
import { render } from "@testing-library/react";
import { AuthProvider } from "../auth/AuthContext";
import { FarmContext } from "../farm/FarmContext";
import { MeContext, MeUpdateContext } from "../session/SessionContext";
import type { Account, Me } from "../api/cluckwork";
import { farmState } from "./fixtures";
import { setStoredToken } from "./jwt";
import { clearAccessToken } from "../auth/tokenStore";

// The default signed-in user for screen tests that don't care who is logged
// in. A test that DOES care passes `me: <fixture>`; one that cares the identity
// is unknown (signed-in-but-/me-failed) passes `me: null` explicitly — see the
// `=== undefined` check below, not `??`, so that null is honoured.
const DEFAULT_ME: Me = {
  id: "u1", email: "test@farm.local", name: null, role: "Admin", language: null,
  preferredStepperUnit: null,
};

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
// #444 — a LIVE Me, not a static provider value: components that patch the
// session profile through useMeUpdate() (StepperUnitSelector) must see their
// own patch reflected back through useMe(), the way the real SessionProvider
// routes it. A bare MeContext.Provider left MeUpdateContext at its no-op
// default, so the optimistic-update wiring could break without a single test
// noticing (adversarial review of #451).
function LiveMe({ initial, children }: { initial: Me | null; children: ReactNode }) {
  const [me, setMe] = useState(initial);
  return (
    <MeContext.Provider value={me}>
      <MeUpdateContext.Provider
        value={(patch) => setMe((prev) => (prev === null ? prev : { ...prev, ...patch }))}>
        {children}
      </MeUpdateContext.Provider>
    </MeContext.Provider>
  );
}

export function renderWithProviders(
  ui: ReactNode,
  opts: { route?: string; token?: Record<string, unknown> | null; farm?: Account; me?: Me | null } = {},
) {
  // A seeded token goes straight into memory, so AuthProvider is authenticated
  // synchronously (no load-time refresh); otherwise the session starts empty.
  if (opts.token) setStoredToken(opts.token);
  else clearAccessToken();

  const farmValue = farmState({ farm: opts.farm ?? null });

  return render(
    <MemoryRouter initialEntries={[opts.route ?? "/"]}>
      <AuthProvider>
        <LiveMe initial={opts.me === undefined ? DEFAULT_ME : opts.me}>
          <FarmContext.Provider value={farmValue}>{ui}</FarmContext.Provider>
        </LiveMe>
      </AuthProvider>
    </MemoryRouter>,
  );
}
