import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Stack, TextField, Typography } from "@mui/material";
import { changePassword, ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { AuthShell } from "../components/AuthShell";
import { BusyButton } from "../components/BusyButton";
import { usePendingAction } from "../components/usePendingAction";

const MIN_LENGTH = 12;

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #283 — the first-run "you must set a new password" screen. ProtectedRoute
// renders this INSTEAD of the app shell (Outlet) whenever the signed-in
// user's token carries must_change_password — the first-run admin created by
// `bootstrap-admin`, or anyone else whose password was force-reset and
// hasn't changed it since. It reuses the SAME /auth/change-password endpoint
// AccountPage's regular change-password form uses: the operator already
// knows the generated temporary password (it was printed to them once) as
// their "current" one. On success, ChangeOwnPasswordAsync clears the flag
// server-side; AuthContext's onTokensChanged callback re-derives
// mustChangePassword from the fresh token, and this screen unmounts itself —
// no navigation call needed, ProtectedRoute just renders the Outlet next
// render.
//
// #833 — D&D "Working desk" (Concept B): shares AuthShell with Login.
export function SetPasswordPage() {
  const { t } = useTranslation("auth");
  const { logout } = useAuth();
  const [temporaryPassword, setTemporaryPassword] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setError(null);

    if (next !== confirm) {
      setError(t("setPasswordMismatchError"));
      return;
    }
    if (next.length < MIN_LENGTH) {
      setError(t("setPasswordTooShortError", { min: MIN_LENGTH }));
      return;
    }

    await run("set-password", async () => {
      try {
        await changePassword({ currentPassword: temporaryPassword, newPassword: next });
        // No further action: the fresh token AuthContext just adopted no
        // longer carries must_change_password, so ProtectedRoute re-renders
        // the normal app shell on its own.
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  return (
    <AuthShell footerNote={t("setPasswordShellFooter")}>
      <Stack component="form" spacing={2} onSubmit={onSubmit}>
        <Typography variant="h2">{t("setPasswordHeading")}</Typography>
        <Typography variant="body2" color="text.secondary">{t("setPasswordHint")}</Typography>
        <TextField
          label={t("temporaryPasswordLabel")}
          type="password"
          value={temporaryPassword}
          onChange={(e) => setTemporaryPassword(e.target.value)}
          autoComplete="current-password"
          required
          slotProps={{ htmlInput: { maxLength: 256 } }}
        />
        <TextField
          label={t("setPasswordNewLabel", { min: MIN_LENGTH })}
          type="password"
          value={next}
          onChange={(e) => setNext(e.target.value)}
          autoComplete="new-password"
          required
          slotProps={{ htmlInput: { minLength: MIN_LENGTH, maxLength: 256 } }}
        />
        <TextField
          label={t("setPasswordConfirmLabel")}
          type="password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          autoComplete="new-password"
          required
          slotProps={{ htmlInput: { maxLength: 256 } }}
        />
        {error && <Alert severity="error">{error}</Alert>}
        <BusyButton type="submit" busy={busy}>
          {busy ? t("setPasswordSubmitting") : t("setPasswordButton")}
        </BusyButton>
        {/* Escape hatch: a gated user must always be able to sign out rather
            than being stuck on this screen (mirrors the API allowlist —
            auth/logout stays reachable while must_change_password is set). */}
        <button type="button" className="link" onClick={() => void logout()}>
          {t("setPasswordSignOut")}
        </button>
      </Stack>
    </AuthShell>
  );
}
