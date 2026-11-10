import { useEffect } from "react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Button } from "@mui/material";
import type { TwoFactorStatus } from "../api/cluckwork";
import { useAuth } from "../auth/useAuth";
import { requestSetUp } from "./account/useTwoFactor";

// #1281 — "Set up two-factor authentication by <deadline>" while the status
// route sends requiredBy and two-factor is off, which it does only for an
// Owner while TwoFactor:RequireForOwners is on. On the Dashboard and on
// Account only (the maintainer, 2026-11-10: "Dashboard and Account only
// (Recommended)"), each passing the status it already reads. It cannot be
// dismissed: it states a deadline. Once the deadline has passed, it hands
// over to the setup screen. Set up opens the setup on Account unless the
// screen handles it.
export function TwoFactorDeadlineBanner({ status, onSetUp }: { status: TwoFactorStatus | null; onSetUp?: () => void }) {
  const { t, i18n } = useTranslation("account");
  const { setMustEnrolTwoFactor } = useAuth();
  const navigate = useNavigate();
  const ms = status && !status.enabled && status.requiredBy ? Date.parse(status.requiredBy) : NaN;
  const passed = ms <= Date.now();

  useEffect(() => {
    if (passed) setMustEnrolTwoFactor(true);
  }, [passed, setMustEnrolTwoFactor]);

  if (Number.isNaN(ms) || passed) return null;
  const when = new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeStyle: "short" }).format(ms);
  const setUp = onSetUp ?? (() => void navigate("/account", { state: requestSetUp() }));
  // role="note": the MUI default "alert" would read to the E2E suite as an error.
  return (
    <Alert severity="warning" role="note" sx={{ mb: 2 }}
      action={<Button color="inherit" size="small" onClick={setUp}>{t("setUpButton")}</Button>}>
      {t("deadlineBanner", { deadline: when })}
    </Alert>
  );
}
