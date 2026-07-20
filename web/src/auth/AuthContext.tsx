import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { login as apiLogin, logout as apiLogout, setOnTokensChanged, setOnUnauthenticated } from "../api/client";
import { currentUserIsAdmin, currentUserRole } from "./claims";
import type { Role } from "./claims";
import { loadTokens } from "./tokenStore";

interface AuthState {
  isAuthenticated: boolean;
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
  const [isAuthenticated, setIsAuthenticated] = useState(() => loadTokens() !== null);
  const [isAdmin, setIsAdmin] = useState(currentUserIsAdmin);
  const [role, setRole] = useState<Role>(currentUserRole);

  // When any authenticated request exhausts its refresh, drop auth state so the
  // router redirects to /login. Token rotation (login or transparent refresh)
  // re-derives the role — the server re-reads roles on every refresh, so a
  // demotion reaches the UI within one token lifetime (codex review of PR #78).
  useEffect(() => {
    setOnUnauthenticated(() => setIsAuthenticated(false));
    setOnTokensChanged(() => {
      setIsAdmin(currentUserIsAdmin());
      setRole(currentUserRole());
    });
    return () => {
      setOnUnauthenticated(null);
      setOnTokensChanged(null);
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
    () => ({ isAuthenticated, isAdmin, role, login, logout }),
    [isAuthenticated, isAdmin, role, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
