import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import {
  login as apiLogin, logout as apiLogout, restoreSession,
  setOnTokensChanged, setOnUnauthenticated,
} from "../api/client";
import { currentUserIsAdmin, currentUserRole } from "./claims";
import type { Role } from "./claims";
import { clearAccessToken, getAccessToken, purgeLegacyTokens } from "./tokenStore";

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
  login: (email: string, password: string) => Promise<void>;
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

  // When any authenticated request exhausts its refresh, drop auth state so the
  // router redirects to /login. Token rotation (login or transparent refresh)
  // re-derives the role — the server re-reads roles on every refresh, so a
  // demotion reaches the UI within one token lifetime (codex review of PR #78).
  useEffect(() => {
    setOnUnauthenticated(() => {
      clearAccessToken();
      setIsAuthenticated(false);
    });
    setOnTokensChanged(() => {
      setIsAdmin(currentUserIsAdmin());
      setRole(currentUserRole());
    });
    return () => {
      setOnUnauthenticated(null);
      setOnTokensChanged(null);
    };
  }, []);

  // Session bootstrap: purge any pre-#145 localStorage token, then (if memory is
  // empty) try a silent refresh via the HttpOnly cookie. Success restores the
  // session in place; failure lands the user cleanly on /login, no error flash.
  useEffect(() => {
    purgeLegacyTokens();
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
          setIsAdmin(currentUserIsAdmin());
          setRole(currentUserRole());
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
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    await apiLogin({ email, password });
    setIsAuthenticated(true);
    setIsAdmin(currentUserIsAdmin());
    setRole(currentUserRole());
  }, []);

  const logout = useCallback(async () => {
    await apiLogout();
    setIsAuthenticated(false);
    setIsAdmin(false);
    setRole("Worker");
  }, []);

  const value = useMemo(
    () => ({ isAuthenticated, isLoading, isAdmin, role, login, logout }),
    [isAuthenticated, isLoading, isAdmin, role, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
