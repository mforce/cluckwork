import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { useLocation, useNavigate } from "react-router";
import { useAuth } from "../auth/useAuth";
import { ApiError } from "../api/client";
import { ThemeToggle } from "../components/ThemeToggle";

interface LocationState {
  from?: { pathname: string };
}

function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) return "Invalid email or password.";
    // Rate limited (#143) — too many attempts from this address.
    if (err.status === 429)
      return "Too many sign-in attempts. Please wait a few minutes and try again.";
  }
  return "Could not sign in. Is the API running?";
}

export function Login() {
  const { login, isAuthenticated, isLoading } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as LocationState | null)?.from?.pathname ?? "/";

  // If the load-time silent refresh (#145) restores a session while we're on
  // /login, don't strand the user on the form — send them to their destination.
  useEffect(() => {
    if (!isLoading && isAuthenticated) navigate(from, { replace: true });
  }, [isLoading, isAuthenticated, from, navigate]);

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(email, password);
      navigate(from, { replace: true });
    } catch (err) {
      setError(messageFor(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="auth">
      <ThemeToggle className="auth-theme" showLabel={false} iconSize={18} />
      <form className="card" onSubmit={onSubmit}>
        <h1>Cluckwork</h1>
        <label>
          Email
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="username"
            required
          />
        </label>
        <label>
          Password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </main>
  );
}
