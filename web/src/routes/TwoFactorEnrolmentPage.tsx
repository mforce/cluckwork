import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Paper, Stack, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { usePendingAction } from "../components/usePendingAction";
import { ProofDialog } from "./account/SecurityPanel";
import { TwoFactorFlow } from "./account/TwoFactorFlow";
import { useTwoFactor } from "./account/useTwoFactor";

// #1281 — an Owner past the grace deadline. ProtectedRoute renders this
// instead of the app shell, whose loads the Owner gate would refuse, so this
// screen calls only what the gate lets through: the status read, step-up,
// start, confirm and logout. It leaves at the location the Owner asked for
// once the status route reads two-factor on, or sends no requiredBy (the
// requirement is off, or the user is no Owner). A requiredBy still in the
// future keeps it: the gate goes by the server's clock, not this browser's.
// A plain page, not AuthShell: AuthShell's form half is too narrow for the
// QR step.
export function TwoFactorEnrolmentPage() {
  const { t } = useTranslation("account");
  const { t: tn } = useTranslation("nav");
  const { logout, setMustEnrolTwoFactor, tokenVersion } = useAuth();
  // A new token (a refresh that adopts another user's, or this user's after a
  // role change) can mean the gate no longer refuses this user.
  const twoFactor = useTwoFactor(tokenVersion);
  const { flow, status, openProof, cancels, dialog: { busy } } = twoFactor;
  const { busy: signingOut, run } = usePendingAction();
  const heading = useRef<HTMLHeadingElement>(null);
  const setUp = useRef<HTMLButtonElement>(null);
  const released = status.data !== null && (status.data.enabled || !status.data.requiredBy);

  useEffect(() => {
    if (released) setMustEnrolTwoFactor(false);
  }, [released, setMustEnrolTwoFactor]);

  useEffect(() => {
    heading.current?.focus();
    document.title = `${t("enrolHeading")}${tn("titleSuffix")}`;
  }, [t, tn]);

  useEffect(() => {
    if (cancels > 0) setUp.current?.focus();
  }, [cancels]);

  return (
    <Box component="main" sx={{ minHeight: "100dvh", p: { xs: 2, md: 4 }, background: "var(--surface-2)" }}>
      <Stack spacing={3} sx={{ maxWidth: "760px", mx: "auto" }}>
        {/* The setup steps wait while Sign out runs. */}
        <Box inert={signingOut}>
          {flow.step === "summary" ? (
            <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 } }}>
              <Stack spacing={2}>
                <Typography variant="h2" component="h1" ref={heading} tabIndex={-1}>{t("enrolHeading")}</Typography>
                <Typography>{t("requiredBody")}</Typography>
                {status.error !== null && (
                  <Alert severity="error" action={<Button color="inherit" onClick={status.retry}>{t("statusRetry")}</Button>}>
                    {t("statusLoadFailed")}
                  </Alert>
                )}
                <Box>
                  <Button variant="contained" ref={setUp} disabled={busy} onClick={() => openProof("start")}>{t("setUpButton")}</Button>
                </Box>
              </Stack>
            </Paper>
          ) : <TwoFactorFlow twoFactor={twoFactor} />}
        </Box>
        <Box>
          <button type="button" className="link" onClick={() => void run("sign-out", logout)} disabled={busy || signingOut}>
            {t("requiredSignOut")}
          </button>
        </Box>
      </Stack>
      <ProofDialog twoFactor={twoFactor} />
    </Box>
  );
}
