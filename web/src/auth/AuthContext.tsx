import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import {
  login as apiLogin, logout as apiLogout, restoreSession,
  setOnTokensChanged, setOnUnauthenticated,
} from "../api/client";
import { currentUserId, currentUserIsAdmin, currentUserMustChangePassword, currentUserRole } from "./claims";
import type { Role } from "./claims";
import { bindFarm, clearAccessToken, getAccessToken, purgeLegacyTokens } from "./tokenStore";
import { clearSplashSeenMarker } from "../session/SessionContext";
import { purgeUnscopedAccountState } from "../lib/accountStorage";
import { canonicalFarmCode, rememberFarmCode } from "./farmCodeCache";

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
    // #586 — bound BEFORE the rememberFarmCode await below, which acquires a
    // cross-tab Web Lock (farmCodeCache.ts:105) and can block behind another
    // tab's login. Nothing should sit between apiLogin's bindAccount
    // (client.ts:250) and the farm binding that must pair with it. bindFarm
    // reads the account binding itself and stores both together, so a slug can
    // never outlive the account it was proven against.
    //
    // A code the server accepted but whose SHAPE this regex rejects
    // (farmCodeCache.ts:16-20 documents that strict direction as the silent
    // one) canonicalises to null and leaves the tab unbound — the same outcome
    // as a cold restore, and never a wrong key.
    bindFarm(canonicalFarmCode(farmCode));
    // #535 — remembered only AFTER apiLogin resolves, so a typo is never stored:
    // a failed sign-in throws out of apiLogin (client.ts:144 `if (!res.ok) throw`)
    // and never reaches this line, and neither does the superseded-session path
    // (client.ts:242 StaleSessionError). The value is normalised inside
    // rememberFarmCode, mirroring the server's own Trim().ToLowerInvariant()
    // lookup, so a capitalised code is still remembered.
    // Awaited rather than fire-and-forget so the roster write is ordered before
    // login's other side effects, and safe to await because rememberFarmCode
    // never rejects.
    await rememberFarmCode(farmCode);
    // #179/codex: a fresh explicit login is a new "per login" for the splash,
    // even in a tab that already dismissed it (or belonged to another user).
    // A silent token refresh never reaches this line, so it's exempt by design.
    clearSplashSeenMarker();
    setIsAuthenticated(true);
    setUnauthenticatedReason(null);
    refreshClaims();
  }, [refreshClaims]);

  const logout = useCallback(async () => {
    // The farm palettes (cluckwork.brand:<slug>) are deliberately NOT cleared
    // here: they are per-farm and device-persistent, the same way
    // cluckwork.theme is (user choice, #149), so this farm's login screen keeps
    // its own colour instead of reverting to the default. Since #586 that is no
    // longer a cross-farm hazard — the pre-paint script reads a palette only
    // for a farm this device can NAME (?farm=, or exactly one remembered code),
    // and "Forget this farm" (#587) removes that farm's palette along with its
    // roster entry.
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
