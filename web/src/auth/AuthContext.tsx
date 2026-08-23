import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import {
  login as apiLogin, logout as apiLogout, restoreSession,
  setOnTokensChanged, setOnUnauthenticated,
} from "../api/client";
import { currentUserId, currentUserIsAdmin, currentUserMustChangePassword, currentUserRole } from "./claims";
import type { Role } from "./claims";
import { clearAccessToken, getAccessToken, purgeLegacyTokens } from "./tokenStore";
import { clearSplashSeenMarker } from "../session/SessionContext";
import { purgeUnscopedAccountState } from "../lib/accountStorage";
import { rememberFarmCode } from "./farmCodeCache";

interface AuthState {
  isAuthenticated: boolean;
  // True until the load-time session bootstrap (silent refresh) resolves, so the
  // router can hold rendering instead of flashing /login before the cookie is
  // exchanged (#145).
  isLoading: boolean;
  // UI visibility only (#73/#103) — every gated endpoint re-checks the role.
  // isAdmin = Owner OR Manager (the corrective/config tier).
  isAdmin: boolean;
  role: Role;
  // #356 — the token's own "sub" claim, for guards that must not depend on
  // /me succeeding (SessionProvider keeps the shell up with me === null on
  // a failed /me).
  userId: string | null;
  // #283 — true while the signed-in user must set a new password before
  // anything else works (the first-run admin, until they do). ProtectedRoute
  // reads this to show the set-password screen instead of the app shell; the
  // API's MustChangePasswordMiddleware is the actual enforcement.
  mustChangePassword: boolean;
  unauthenticatedReason: string | null;
  login: (farmCode: string, email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

// eslint-disable-next-line react-refresh/only-export-components
export const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  // The access token is memory-only (#145), so on a fresh load it's absent and
  // we bootstrap; when it's already set (e.g. a test seed) we're ready at once.
  const [isAuthenticated, setIsAuthenticated] = useState(() => getAccessToken() !== null);
  const [isLoading, setIsLoading] = useState(() => getAccessToken() === null);
  const [isAdmin, setIsAdmin] = useState(currentUserIsAdmin);
  const [role, setRole] = useState<Role>(currentUserRole);
  const [userId, setUserId] = useState<string | null>(currentUserId);
  const [mustChangePassword, setMustChangePassword] = useState(currentUserMustChangePassword);
  const [unauthenticatedReason, setUnauthenticatedReason] = useState<string | null>(null);

  // One place that re-derives every claim-decoded field from the current
  // token, called everywhere a token pair changes (login, transparent
  // refresh, load-time bootstrap) — a spot that set isAdmin/role individually
  // is exactly how a future new claim (like this one) gets forgotten in one
  // of the call sites.
  const refreshClaims = useCallback(() => {
    setIsAdmin(currentUserIsAdmin());
    setRole(currentUserRole());
    setMustChangePassword(currentUserMustChangePassword());
    setUserId(currentUserId());
  }, []);

  // When any authenticated request exhausts its refresh, drop auth state so the
  // router redirects to /login. Token rotation (login or transparent refresh)
  // re-derives the role — the server re-reads roles on every refresh, so a
  // demotion reaches the UI within one token lifetime (codex review of PR #78).
  useEffect(() => {
    setOnUnauthenticated((title) => {
      clearAccessToken();
      setIsAuthenticated(false);
      setUnauthenticatedReason(title ?? null);
    });
    setOnTokensChanged(refreshClaims);
    return () => {
      setOnUnauthenticated(null);
      setOnTokensChanged(null);
    };
  }, [refreshClaims]);

  // Session bootstrap: purge any pre-#145 localStorage token, then (if memory is
  // empty) try a silent refresh via the HttpOnly cookie. Success restores the
  // session in place; failure lands the user cleanly on /login, no error flash.
  useEffect(() => {
    purgeLegacyTokens();
    // #535 — the pre-namespacing per-account keys. Dropped rather than migrated:
    // the app cannot attribute them to a farm, so migrating would be a guess.
    purgeUnscopedAccountState();
    if (getAccessToken() !== null) {
      setIsLoading(false);
      return;
    }
    let cancelled = false;
    void restoreSession()
      .then((restored) => {
        if (cancelled) return;
        if (restored) {
          setIsAuthenticated(true);
          refreshClaims();
        }
      })
      // restoreSession never rejects today, but clearing isLoading here too keeps
      // a future throw from wedging the router on a permanent blank screen.
      .finally(() => {
        if (!cancelled) setIsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [refreshClaims]);

  const login = useCallback(async (farmCode: string, email: string, password: string) => {
    await apiLogin({ farmCode, email, password });
    // #535 — remembered only AFTER apiLogin resolves, so a typo is never stored:
    // a failed sign-in throws out of apiLogin (client.ts:144 `if (!res.ok) throw`)
    // and never reaches this line, and neither does the superseded-session path
    // (client.ts:242 StaleSessionError). The value is normalised inside
    // rememberFarmCode, mirroring the server's own Trim().ToLowerInvariant()
    // lookup, so a capitalised code is still remembered.
    rememberFarmCode(farmCode);
    // #179/codex: a fresh explicit login is a new "per login" for the splash,
    // even in a tab that already dismissed it (or belonged to another user).
    // A silent token refresh never reaches this line, so it's exempt by design.
    clearSplashSeenMarker();
    setIsAuthenticated(true);
    setUnauthenticatedReason(null);
    refreshClaims();
  }, [refreshClaims]);

  const logout = useCallback(async () => {
    // The farm palette (cluckwork.brand) is deliberately NOT cleared here: it
    // stays device-persistent, the same way cluckwork.theme is (user choice,
    // #149), so the login screen keeps showing the last palette rather than
    // reverting to the default. That persistence means a multi-farm device
    // shows the previous farm's palette on the next login — the "single-farm
    // deployment assumption this once leaned on is gone now that accounts
    // coexist (#530) — the per-farm fix for that is tracked in #586.
    await apiLogout();
    setIsAuthenticated(false);
    setIsAdmin(false);
    setRole("Worker");
    setMustChangePassword(false);
    setUserId(null);
    setUnauthenticatedReason(null);
  }, []);

  const value = useMemo(
    () => ({
      isAuthenticated, isLoading, isAdmin, role, userId, mustChangePassword, unauthenticatedReason, login, logout,
    }),
    [isAuthenticated, isLoading, isAdmin, role, userId, mustChangePassword, unauthenticatedReason, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
