import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Paper, Stack, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ProofDialog } from "./account/SecurityPanel";
import { TwoFactorFlow } from "./account/TwoFactorFlow";
import { useTwoFactor } from "./account/useTwoFactor";

// #1281 — an Owner past the grace deadline. ProtectedRoute renders this
// instead of the app shell, whose loads the Owner gate would refuse, so this
// screen calls only what the gate lets through: the status read, step-up,
// start, confirm and logout. It leaves once the status route reads two-factor
// on, at the location the Owner asked for.
export function TwoFactorEnrolmentPage() {
  const { t } = useTranslation("account");
  const { logout, setMustEnrolTwoFactor } = useAuth();
  const twoFactor = useTwoFactor();
  const { flow, status, openProof, dialog: { busy } } = twoFactor;
  const enrolled = status.data?.enabled === true;

  useEffect(() => {
    if (enrolled) setMustEnrolTwoFactor(false);
  }, [enrolled, setMustEnrolTwoFactor]);

  return (
    <Box component="main" sx={{ minHeight: "100dvh", p: { xs: 2, md: 4 }, background: "var(--surface-2)" }}>
      <Stack spacing={3} sx={{ maxWidth: "760px", mx: "auto" }}>
        {flow.step === "summary" ? (
          <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 } }}>
            <Stack spacing={2}>
              <Typography variant="h2">{t("enrolHeading")}</Typography>
              <Typography>{t("requiredBody")}</Typography>
              {status.error !== null && (
                <Alert severity="error" action={<Button color="inherit" onClick={status.retry}>{t("statusRetry")}</Button>}>
                  {t("statusLoadFailed")}
                </Alert>
              )}
              <Box>
                <Button variant="contained" disabled={busy} onClick={() => openProof("start")}>{t("setUpButton")}</Button>
              </Box>
            </Stack>
          </Paper>
        ) : <TwoFactorFlow twoFactor={twoFactor} />}
        <Box>
          <button type="button" className="link" onClick={() => void logout()} disabled={busy}>{t("requiredSignOut")}</button>
        </Box>
      </Stack>
      <ProofDialog twoFactor={twoFactor} />
    </Box>
  );
}
