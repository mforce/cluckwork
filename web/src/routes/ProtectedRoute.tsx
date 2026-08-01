import { Navigate, Outlet, useLocation } from "react-router";
import { useAuth } from "../auth/useAuth";
import { SetPasswordPage } from "./SetPasswordPage";

export function ProtectedRoute() {
  const { isAuthenticated, isLoading, mustChangePassword } = useAuth();
  const location = useLocation();

  // Hold rendering while the load-time silent refresh runs (#145) so we don't
  // flash /login before the cookie has a chance to restore the session.
  if (isLoading) {
    return null;
  }
  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location }} />;
  }
  // #283 — every route behind this gate is unreachable until the pending
  // password change is done: rendered here, OUTSIDE SessionProvider/AppLayout
  // (which read /me + /account — both blocked server-side anyway while
  // must_change_password is set), so no other API call is even attempted.
  // Clearing the flag needs no navigation: AuthContext re-derives it from the
  // fresh token on change-password success, and this same location then
  // renders the Outlet on the next render.
  if (mustChangePassword) {
    return <SetPasswordPage />;
  }
  return <Outlet />;
}
