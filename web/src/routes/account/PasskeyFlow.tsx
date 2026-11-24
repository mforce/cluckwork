import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Paper, Stack, TextField, Typography } from "@mui/material";
import { useAuth } from "../../auth/useAuth";
import { BusyButton } from "../../components/BusyButton";
import { DialogError } from "../../components/DialogError";
import { useFarm } from "../../farm/useFarm";
import { useMe } from "../../session/SessionContext";
import type { Passkeys } from "./usePasskeys";

// #1283 "Registration A": after the step-up, the create step takes the
// Account page, as the app's enrolment does (TwoFactorFlow).
export function PasskeyFlow({ passkeys, onSetUpApp }: { passkeys: Passkeys; onSetUpApp: () => void }) {
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { twoFactor: enrolled } = useAuth();
  const email = useMe()?.email;
  const farm = useFarm().farm?.name;
  const { flow, name, setName, onCreate, leaveFlow, dialog: { busy, isPending, errors } } = passkeys;
  // The control that opened this step has left the page, so its heading takes focus.
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    headingRef.current?.focus();
  }, [flow.step]);

  let body;
  if (flow.step === "created") {
    body = (
      <Stack spacing={2}>
        <Typography variant="h2" ref={headingRef} tabIndex={-1}>{t("passkeyCreatedHeading")}</Typography>
        <Alert severity="success">{t("passkeyReady", { name: flow.name })}</Alert>
        <Typography>{t("passkeyReadyHint")}</Typography>
        <Typography color="text.secondary">{t("passkeyManageHint")}</Typography>
        <Box><Button variant="contained" onClick={leaveFlow}>{t("doneButton")}</Button></Box>
      </Stack>
    );
  } else if (flow.step === "unsupported") {
    body = (
      <Stack spacing={2}>
        <Typography variant="h2" ref={headingRef} tabIndex={-1}>{t("passkeyUnsupportedHeading")}</Typography>
        <Alert severity="warning">{t("passkeyUnsupported")}</Alert>
        <Typography>{t(enrolled ? "passkeyUnsupportedHintEnrolled" : "passkeyUnsupportedHint")}</Typography>
        <Stack direction="row" spacing={2} sx={{ flexWrap: "wrap", gap: 1 }}>
          <Button variant="contained" onClick={leaveFlow}>{t("backToSecurityButton")}</Button>
          {!enrolled && (
            <Button variant="outlined" onClick={() => { leaveFlow(); onSetUpApp(); }}>{t("setUpTitle")}</Button>
          )}
        </Stack>
      </Stack>
    );
  } else if (flow.step === "create") {
    const waiting = isPending("create");
    body = (
      <Stack spacing={2} component="form" onSubmit={onCreate} sx={{ maxWidth: "32rem" }}>
        <Typography variant="h2" ref={headingRef} tabIndex={-1}>{t("createPasskeyHeading")}</Typography>
        <Typography color="text.secondary">{t("createPasskeyHint")}</Typography>
        {flow.cancelled && <Alert severity="warning">{t("passkeyCancelled")}</Alert>}
        <TextField label={t("passkeyNameLabel")} helperText={t("passkeyNameHint")} value={name} required disabled={busy}
          onChange={(e) => setName(e.target.value)} slotProps={{ htmlInput: { maxLength: 64 } }} />
        {email && farm && (
          <Typography variant="caption" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>
            {t("passkeyDeviceName", { email, farm })}
          </Typography>
        )}
        {flow.first && <Alert severity="warning" role="note">{t("passkeySessionWarning")}</Alert>}
        {waiting && <Alert severity="info">{t("passkeyWaiting")}</Alert>}
        <DialogError errors={errors} scope="create" />
        <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
          <BusyButton variant="contained" type="submit" busy={waiting} disabled={busy}>
            {t(flow.grant === null ? "startAgainButton" : flow.cancelled ? "tryAgainButton" : "createPasskeyButton")}
          </BusyButton>
          <button type="button" className="link" onClick={leaveFlow} disabled={busy}>{tc("cancel")}</button>
        </Stack>
      </Stack>
    );
  }

  return (
    <Box component="section" sx={{ maxWidth: "760px" }}>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>{t("flowEyebrow")}</Typography>
      <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 }, mt: 1 }}>{body}</Paper>
    </Box>
  );
}
