import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { login as apiLogin, logout as apiLogout, setOnUnauthenticated } from "../api/client";
import { loadTokens } from "./tokenStore";

interface AuthState {
  isAuthenticated: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

// eslint-disable-next-line react-refresh/only-export-components
export const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isAuthenticated, setIsAuthenticated] = useState(() => loadTokens() !== null);

  // When any authenticated request exhausts its refresh, drop auth state so the
  // router redirects to /login.
  useEffect(() => {
    setOnUnauthenticated(() => setIsAuthenticated(false));
    return () => setOnUnauthenticated(null);
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    await apiLogin({ email, password });
    setIsAuthenticated(true);
  }, []);

  const logout = useCallback(async () => {
    await apiLogout();
    setIsAuthenticated(false);
  }, []);

  const value = useMemo(
    () => ({ isAuthenticated, login, logout }),
    [isAuthenticated, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
