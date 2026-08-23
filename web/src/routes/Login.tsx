import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth/useAuth";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { ThemeToggle } from "../components/ThemeToggle";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { canonicalFarmCode, readFarmCodes } from "../auth/farmCodeCache";

interface LocationState {
  from?: { pathname: string };
}

// #283 follow-up — the error code a failed sign-in carries when the default
// account has no Owner. Mirrors AuthEndpoints.NoOwnerProvisionedCode; it
// rides the ProblemDetails `title`, which parseError puts on ApiError.title.
const NO_OWNER_PROVISIONED = "Auth.NoOwnerProvisioned";
const CREDENTIALS_SUPERSEDED = "Auth.CredentialsSuperseded";
const ACCOUNT_DISABLED = "Auth.AccountDisabled";
// #532 — both ride the ProblemDetails `title` on a 401, so they MUST be matched
// before messageFor's generic 401 branch. Without that, a suspended farm's staff
// are told their password is wrong, which is the exact outcome the owner's
// decision to use a distinct code exists to avoid.
const UNKNOWN_FARM_CODE = "Auth.UnknownFarmCode";
const FARM_SUSPENDED = "Auth.FarmSuspended";
// #532 — several per-farm cookies, no selector: the tab was torn down and the
// user lands here to pick a farm and sign in (the picker lands below).
const FARM_SELECTION_REQUIRED = "Auth.FarmSelectionRequired";

// Matched on the code, never on the message: the copy is translated and the
// server's English detail is not what identifies the case.
//
// The status is checked too. This is a 401 like any other sign-in failure, and
// pinning that keeps the branch from firing on some future non-401 response
// that happens to reuse the title.
function isNoOwnerProvisioned(err: unknown): boolean {
  return err instanceof ApiError
    && err.status === 401
    && err.title === NO_OWNER_PROVISIONED;
}

// MODULE-LEVEL — called from onSubmit's catch handler, not from render, so the
// useTranslation hook is not in scope here. The imperative i18n singleton
// (already initialised, already holding the resolved language) is the correct
// tool outside render (#182).
function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    // #532 — ahead of the generic 401 below, which would otherwise report a
    // suspended farm or a mistyped farm code as a bad password.
    if (err.title === UNKNOWN_FARM_CODE) return i18n.t("auth:unknownFarmCode");
    if (err.title === FARM_SUSPENDED) return i18n.t("auth:farmSuspended");
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

export function Login() {
  const { t } = useTranslation("auth");
  const { login, isAuthenticated, isLoading, unauthenticatedReason } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as LocationState | null)?.from?.pathname ?? "/";

  // If the load-time silent refresh (#145) restores a session while we're on
  // /login, don't strand the user on the form — send them to their destination.
  useEffect(() => {
    if (!isLoading && isAuthenticated) navigate(from, { replace: true });
  }, [isLoading, isAuthenticated, from, navigate]);

  const [searchParams] = useSearchParams();
  // #535 prefill order, first match wins:
  //   1. ?farm=<slug>, validated. An INVALID value is not a match, so it falls
  //      through to the cache — and it is never rendered and never truncated. No
  //      error is shown: an anonymous visitor who mistyped a URL is told nothing
  //      about which farms exist.
  //   2. the device's remembered codes — exactly one prefills, several offer a picker.
  //   3. empty.
  //
  // Resolved ONCE in useState initialisers, never in an effect: the field must be
  // right on first paint, and an effect-based prefill races the user typing.
  const [urlFarmCode] = useState(() => canonicalFarmCode(searchParams.get("farm")));
  // Not consulted AT ALL when the query parameter supplied a usable code, which is
  // what #535 requires — hence a gate here rather than a filter later.
  const [rememberedCodes] = useState(() => (urlFarmCode === null ? readFarmCodes() : []));
  const [farmCode, setFarmCode] = useState(
    () => urlFarmCode ?? (rememberedCodes.length === 1 ? rememberedCodes[0] : ""),
  );
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  useEffect(() => {
    if (unauthenticatedReason === CREDENTIALS_SUPERSEDED)
      setError(t("credentialsSuperseded"));
    else if (unauthenticatedReason === ACCOUNT_DISABLED)
      setError(t("accountDisabled"));
    // #532 — torn down because this browser holds several farms' sessions and
    // the tab could not name one. The form's farm-code field IS the picker for
    // now; the dedicated picker is #535.
    else if (unauthenticatedReason === FARM_SELECTION_REQUIRED)
      setError(t("farmSelectionRequired"));
    // #532 — apiFetch tears a session down and preserves the 401 title when a
    // SUSPENSION is the reason (AuthContext stores it), so a user kicked out
    // mid-shift lands here already signed out. Without this branch the page is
    // a blank form until they submit a second time. Reuses the same
    // auth:farmSuspended copy the login POST path renders.
    else if (unauthenticatedReason === FARM_SUSPENDED)
      setError(t("farmSuspended"));
  }, [t, unauthenticatedReason]);

  // #283 follow-up — a freshly migrated default account has base reference data
  // but no Owner, because no credential is ever migration-baked, so there is
  // nobody for the operator to sign in as and the form used to say nothing
  // about why.
  //
  // Learned from the sign-in ATTEMPT, not from a status call on mount. An
  // earlier version polled a dedicated endpoint here; that answered anyone who
  // asked, and reached the database on every anonymous page load throughout the
  // window before setup. The server now reports it on the failure it already
  // returns, so nothing extra is requested and nobody who is not actually
  // trying to sign in is told anything.
  const [needsSetup, setNeedsSetup] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // The hook's ref skips a same-tick re-submit; setError stays inside the
    // action so a skipped run never wipes a visible failure message.
    await run("signin", async () => {
      setError(null);
      try {
        await login(farmCode, email, password);
        navigate(from, { replace: true });
      } catch (err) {
        // The server distinguishes "the default account has no Owner" from an
        // ordinary wrong credential. Show the setup notice for the first and
        // suppress the generic denial: for the operator this exists to help,
        // who holds no credentials at all yet, "invalid email or password"
        // describes a problem with their typing that they do not have.
        //
        // Not the same as "nothing was wrong with what was typed" — an earlier
        // version of this comment said that, and it is false when a seeded
        // non-Owner user simply mistypes their own password before an Owner
        // exists (PR #363 review). They see this notice too; it is still true
        // that there is no administrator, which is why the copy says exactly
        // that and no more.
        const noOwner = isNoOwnerProvisioned(err);
        setNeedsSetup(noOwner);
        setError(noOwner ? null : messageFor(err));
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
            {/* No command is shown, deliberately. Earlier drafts printed the
                setup invocation here and it was wrong twice over: the bare verb
                was not runnable at all, and the corrected version had to show
                two forms because the app cannot know how it was deployed. A
                login screen is also the wrong place to publish deployment shape
                to anonymous visitors. State the situation and point at the
                person who can fix it; the exact steps live in the README. */}
            <p>{t("noAdminYet")}</p>
            <p>{t("noAdminYetHint")}</p>
          </div>
        )}
        {urlFarmCode !== null && (
          // #535 — a same-origin link ?farm=attacker-farm would otherwise silently
          // replace the operator's own farm code in a field they did not type,
          // while the password manager autofills for the ORIGIN. Naming the farm
          // turns a silent substitution into a visible one. NOT a complete fix:
          // the username is still not farm-qualified (epic #530 requires that;
          // tracked in its own issue).
          <p className="auth-farm-source" role="status">
            {t("farmFromLink", { farmCode: urlFarmCode })}
          </p>
        )}
        {rememberedCodes.length > 1 && (
          <div className="auth-farm-picker">
            <p id="farm-picker-label">{t("recentFarms")}</p>
            <div role="group" aria-labelledby="farm-picker-label">
              {rememberedCodes.map((code) => (
                // type="button" so a tap fills the field instead of submitting.
                // The code is button TEXT: React escapes it, and it has passed the
                // slug regex, so it carries no markup and no URL-significant char.
                <button key={code} type="button" onClick={() => setFarmCode(code)}>
                  {code}
                </button>
              ))}
            </div>
          </div>
        )}
        <label>
          {t("farmCode")}
          <input
            type="text"
            value={farmCode}
            onChange={(e) => setFarmCode(e.target.value)}
            // #532 — the server folds case, so these only stop the user seeing a
            // code they did not type. autoCapitalize is the one that matters:
            // iOS and Android capitalise the first letter of a plain text input
            // by default, and farm codes are lowercase-only.
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            maxLength={32}
            required
          />
        </label>
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
