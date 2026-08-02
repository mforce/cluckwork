import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { useLocation, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth/useAuth";
import { ApiError, getProvisioningStatus } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { ThemeToggle } from "../components/ThemeToggle";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";

interface LocationState {
  from?: { pathname: string };
}

// MODULE-LEVEL — called from onSubmit's catch handler, not from render, so the
// useTranslation hook is not in scope here. The imperative i18n singleton
// (already initialised, already holding the resolved language) is the correct
// tool outside render (#182).
function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) return i18n.t("auth:invalidCredentials");
    // Rate limited (#143) — too many attempts from this address.
    if (err.status === 429) return i18n.t("auth:tooManyAttempts");
    // #309 — an oversized-credential validation error (400, e.g. a >256-char
    // email/password). ApiError already carries the server's real, non-
    // enumerating message (parseError flattens body.detail / body.errors), so
    // show that instead of the generic apiDown copy, which would misleadingly
    // suggest the API itself is unreachable.
    if (err.status === 400) return err.message || i18n.t("auth:apiDown");
    // #309 — the request body exceeded the endpoint's byte cap (413), which in
    // practice means an implausibly long email/password.
    if (err.status === 413) return i18n.t("auth:credentialsTooLong");
  }
  return i18n.t("auth:apiDown");
}

// The operator-facing form (the container image's ENTRYPOINT already supplies
// `dotnet Cluckwork.Api.dll`, so the verb is passed on its own — README's
// "Run the whole app (Docker)" section). Never translated.
const BOOTSTRAP_COMMAND = "bootstrap-admin --email you@example.com";

export function Login() {
  const { t } = useTranslation("auth");
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
  const { busy, run } = usePendingAction();

  // #283 follow-up — a freshly migrated instance has base reference data but no
  // users (no credential is ever migration-baked), so this form cannot succeed
  // and previously gave the operator nothing to go on. Ask once, on mount.
  //
  // Defaults to false and only an explicit `provisioned: false` flips it, so an
  // unreachable or older API renders the ordinary form rather than a hint that
  // might be wrong. Deliberately no retry and no spinner: this is an aside, and
  // it must never delay or interfere with someone who already has credentials.
  const [needsSetup, setNeedsSetup] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getProvisioningStatus()
      .then((provisioned) => {
        if (!cancelled) setNeedsSetup(!provisioned);
      })
      .catch(() => {
        // Unreachable API — say nothing. The sign-in attempt itself surfaces
        // that case with a real message (auth:apiDown).
      });
    return () => {
      cancelled = true;
    };
  }, []);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // The hook's ref skips a same-tick re-submit; setError stays inside the
    // action so a skipped run never wipes a visible failure message.
    await run("signin", async () => {
      setError(null);
      try {
        await login(email, password);
        navigate(from, { replace: true });
      } catch (err) {
        setError(messageFor(err));
      }
    });
  }

  return (
    <main className="auth">
      <ThemeToggle className="auth-theme" showLabel={false} iconSize={18} />
      <form className="card" onSubmit={onSubmit}>
        <h1>{t("title")}</h1>
        {needsSetup && (
          <div className="auth-setup" role="status">
            <p>{t("noAdminYet")}</p>
            {/* The command is the whole point of the hint, so it is selectable
                text in a <code>, not baked into a translated sentence — it must
                never be localised, and an operator must be able to copy it. */}
            <code>{BOOTSTRAP_COMMAND}</code>
            <p>{t("noAdminYetHint")}</p>
          </div>
        )}
        <label>
          {t("email")}
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="username"
            maxLength={256}
            required
          />
        </label>
        <label>
          {t("password")}
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            maxLength={256}
            required
          />
        </label>
        {error && <p className="error">{error}</p>}
        <BusyButton type="submit" busy={busy}>
          {busy ? t("signingIn") : t("signIn")}
        </BusyButton>
      </form>
    </main>
  );
}
