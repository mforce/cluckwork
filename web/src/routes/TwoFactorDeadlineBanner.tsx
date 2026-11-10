import { useEffect } from "react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Button } from "@mui/material";
import { getTwoFactorStatus } from "../api/cluckwork";
import { useAuth } from "../auth/useAuth";
import { useLatestLoad } from "../components/useLatestLoad";
import { useRecoveryCodesSlot } from "./account/recoveryCodes";
import { requestSetUp } from "./account/useTwoFactor";

// #1281 — "Set up two-factor authentication by <deadline>" while the status
// route sends requiredBy and two-factor is off, which it does only for an
// Owner while TwoFactor:RequireForOwners is on. It cannot be dismissed: it
// states a deadline. Once the deadline has passed, it hands over to the setup
// screen. Reloaded when a code-issuing request settles, as the Account
// status is, so it goes once the Owner enrols.
export function TwoFactorDeadlineBanner() {
  const { t, i18n } = useTranslation("account");
  const { setMustEnrolTwoFactor } = useAuth();
  const navigate = useNavigate();
  const { settled } = useRecoveryCodesSlot();
  const status = useLatestLoad([settled], getTwoFactorStatus);
  const state = status.data;
  const ms = state && !state.enabled && state.requiredBy ? Date.parse(state.requiredBy) : NaN;
  const passed = ms <= Date.now();

  useEffect(() => {
    if (passed) setMustEnrolTwoFactor(true);
  }, [passed, setMustEnrolTwoFactor]);

  if (Number.isNaN(ms) || passed) return null;
  const when = new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeStyle: "short" }).format(ms);
  // role="note": the MUI default "alert" would read to the E2E suite as an error on every screen.
  return (
    <Alert severity="warning" role="note" sx={{ mb: 2 }}
      action={<Button color="inherit" size="small" onClick={() => void navigate("/account", { state: requestSetUp() })}>{t("setUpButton")}</Button>}>
      {t("deadlineBanner", { deadline: when })}
    </Alert>
  );
}
