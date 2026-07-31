import { useState } from "react";
import type { FormEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { changePassword, ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { usePendingAction } from "../components/usePendingAction";
import { LanguageSelector } from "../session/LanguageSelector";
import { SUPPORTED_LANGUAGES } from "../i18n";
import { roleLabel } from "../i18n/enums";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const MIN_LENGTH = 12;

// #165 — the one self-service surface: every role can change their own password
// by proving the current one. Changing it signs out this account's OTHER devices
// (the server revokes every refresh token) while keeping this one signed in.
export function AccountPage() {
  const { t } = useTranslation("account");
  const { role } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // State check first so an Enter-key re-submit mid-flight cannot clear the
    // messages; the hook's ref closes the same-tick window the state misses.
    if (busy) return;
    setError(null);
    setMessage(null);

    // Caught here so a typo never costs a round-trip (the server checks too).
    if (next !== confirm) {
      setError(t("passwordMismatchError"));
      return;
    }
    if (next.length < MIN_LENGTH) {
      setError(t("passwordTooShortError", { min: MIN_LENGTH }));
      return;
    }

    await run("change-password", async () => {
      try {
        // No key threaded here: the server exempts this route from the response
        // cache (#165 review), since replaying it would hand back the access token
        // without the rotated refresh cookie.
        await changePassword({ currentPassword: current, newPassword: next });
        setMessage(t("passwordChangedMessage"));
        setCurrent("");
        setNext("");
        setConfirm("");
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  return (
    <section>
      <div className="page-head">
        <h2>{t("heading")}</h2>
      </div>
      <p className="muted">
        <Trans ns="account" i18nKey="roleLine" values={{ role: roleLabel(role) }} components={{ strong: <strong /> }} />
      </p>

      {SUPPORTED_LANGUAGES.length > 1 && (
        <section>
          <h3>{t("preferences")}</h3>
          <p className="hint">{t("languageHint")}</p>
          <LanguageSelector />
        </section>
      )}

      <h3>{t("changePasswordHeading")}</h3>
      <p className="muted">
        {t("changePasswordHint")}
      </p>
      <form className="inline-form" onSubmit={onSubmit}>
        <label>{t("currentPasswordLabel")}
          <input type="password" value={current} required autoComplete="current-password"
            maxLength={256}
            onChange={(e) => setCurrent(e.target.value)} />
        </label>
        <label>{t("newPasswordLabel", { min: MIN_LENGTH })}
          <input type="password" value={next} required minLength={MIN_LENGTH} maxLength={256}
            autoComplete="new-password"
            onChange={(e) => setNext(e.target.value)} />
        </label>
        <label>{t("confirmPasswordLabel")}
          <input type="password" value={confirm} required autoComplete="new-password"
            maxLength={256}
            onChange={(e) => setConfirm(e.target.value)} />
        </label>
        {error && <p className="error">{error}</p>}
        {message && <p className="success">{message}</p>}
        <div className="dialog-foot">
          <BusyButton type="submit" busy={busy}>{t("changePasswordButton")}</BusyButton>
        </div>
      </form>
    </section>
  );
}
