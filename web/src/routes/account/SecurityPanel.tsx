import { useId } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown } from "lucide-react";
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, DialogActions, Divider, Stack, Typography,
} from "@mui/material";
import { useAuth } from "../../auth/useAuth";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { StepUpProofFields } from "../../components/StepUpProofFields";
import { useMe } from "../../session/SessionContext";
import { StatusDot } from "../dashboard/StatusDot";
import { SECURITY_SUMMARY_ID, useRecoveryCodesSlot } from "./recoveryCodes";
import { LOW_RECOVERY_CODES } from "./useTwoFactor";
import type { ProofAction, TwoFactor } from "./useTwoFactor";

// #1275 "Account": the status, the recovery codes left, Regenerate and Turn
// off together in one accordion.
export function SecurityPanel({ twoFactor }: { twoFactor: TwoFactor }) {
  const { t } = useTranslation("account");
  const { status, message, openProof } = twoFactor;
  const state = status.data;
  // One code-issuing request at a time, and none while codes wait to be
  // saved; Turn off waits too, so a late set never lands on a disabled factor.
  const { busy } = useRecoveryCodesSlot();
  const low = state?.enabled && state.recoveryCodesLeft <= LOW_RECOVERY_CODES;

  return (
    <Accordion defaultExpanded disableGutters>
      <AccordionSummary id={SECURITY_SUMMARY_ID} expandIcon={<ChevronDown size={18} aria-hidden />}>
        <Typography variant="h3" component="span">{t("securityHeading")}</Typography>
      </AccordionSummary>
      <AccordionDetails>
        <Stack spacing={2}>
          <Box role="status" sx={{ "&:empty": { display: "none" } }}>
            {message && <Alert severity="success" role="none">{message}</Alert>}
          </Box>
          {status.error !== null && (
            <Alert severity="error" action={<Button color="inherit" onClick={status.retry}>{t("statusRetry")}</Button>}>
              {t("statusLoadFailed")}
            </Alert>
          )}
          {state && (
            <Row action={!state.enabled && <Button variant="contained" disabled={busy} onClick={() => openProof("start")}>{t("setUpButton")}</Button>}>
              <Typography variant="h3" component="h3">{t("twoFactorHeading")}</Typography>
              <Typography variant="body2" color="text.secondary">{t("twoFactorHint")}</Typography>
              {state.enabled
                ? <StatusDot label={t("twoFactorOn")} forceColor="var(--success)" />
                : <StatusDot label={t("twoFactorOff")} />}
            </Row>
          )}
          {state && !state.enabled && <Typography variant="body2" color="text.secondary">{t("twoFactorOffHint")}</Typography>}
          {state?.enabled && (
            <>
              <Row action={<Button variant="outlined" disabled={busy} onClick={() => openProof("regenerate")}>{t("regenerateButton")}</Button>}>
                <Typography variant="h3" component="h3">{t("recoveryCodesHeading")}</Typography>
                <Typography>{t("recoveryCodesLeft", { count: state.recoveryCodesLeft })}</Typography>
                <Typography variant="caption" color="text.secondary">{t("recoveryCodesCaption")}</Typography>
              </Row>
              {low && <Alert severity="warning" role="note">{t("recoveryCodesLow", { count: state.recoveryCodesLeft })}</Alert>}
              <Row action={<Button variant="outlined" disabled={busy} onClick={() => openProof("start")}>{t("replaceButton")}</Button>}>
                <Typography variant="body2" color="text.secondary">{t("replaceHint")}</Typography>
              </Row>
              <Row action={<Button color="error" disabled={busy} onClick={() => openProof("turn-off")}>{t("turnOffButton")}</Button>}>
                <Typography variant="body2" color="text.secondary">{t("turnOffHint")}</Typography>
              </Row>
            </>
          )}
        </Stack>
      </AccordionDetails>
    </Accordion>
  );
}

function Row({ children, action }: { children: ReactNode; action?: ReactNode }) {
  return (
    <Box sx={{
      display: "flex", flexWrap: { xs: "wrap", md: "nowrap" }, alignItems: { md: "center" }, gap: 2,
      py: 1.5, borderTop: "1px solid var(--rule)",
    }}>
      <Stack spacing={0.5} sx={{ flex: "1 1 14rem", minWidth: 0 }}>{children}</Stack>
      {action && <Box sx={{ flexShrink: 0 }}>{action}</Box>}
    </Box>
  );
}

function copyFor(action: ProofAction, enrolled: boolean) {
  if (action === "regenerate") return { title: "regenerateTitle", body: "regenerateBody", submit: "regenerateButton" } as const;
  if (action === "turn-off") return { title: "turnOffTitle", body: "turnOffBody", submit: "turnOffButton" } as const;
  return enrolled
    ? { title: "replaceTitle", body: "replaceBody", submit: "continueButton" } as const
    : { title: "setUpTitle", body: "setUpBody", submit: "continueButton" } as const;
}

// The lab's proof(): "Confirm it's you", what to enter and who is signed in,
// then the password and, once two-factor is on, the code. Start again opens it
// from the enrolment screen too, so the page renders it outside both. Its copy
// reads the session's two_factor claim, the same fact StepUpProofFields reads
// for the code field, so the words and the fields always agree.
export function ProofDialog({ twoFactor }: { twoFactor: TwoFactor }) {
  const bodyId = useId();
  const { t } = useTranslation("account");
  const { t: tu } = useTranslation("users");
  const { t: tc } = useTranslation("common");
  const { twoFactor: enrolled } = useAuth();
  const me = useMe();
  const { action, proof, setProof, closeProof, onProof, dialog: { busy, isPending, errors } } = twoFactor;
  const copy = action && copyFor(action, enrolled);
  return (
    <Dialog
      open={action !== null}
      title={copy ? t(copy.title) : ""}
      onClose={closeProof}
      closeDisabled={busy}
      describedBy={bodyId}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeProof} disabled={busy}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color={action === "turn-off" ? "error" : "primary"} disabled={busy}
            busy={action !== null && isPending(action)}>
            {copy && t(copy.submit)}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onProof }}
    >
      <Stack spacing={2}>
        <Typography id={bodyId} sx={{ lineHeight: 1.55 }}>{copy && t(copy.body)}</Typography>
        <Divider />
        <Typography component="h3" variant="h3">{tu("confirmItsYouHeading")}</Typography>
        <p className="muted">{t(enrolled ? "proofHint" : "proofHintPassword")}</p>
        {me && (
          <Typography variant="caption" sx={{ overflowWrap: "anywhere" }}>
            {tu("signedInAsCaption", { email: me.email })}
          </Typography>
        )}
        <StepUpProofFields
          label={tu("stepUpFieldLabel")}
          codeLabel={tu("stepUpCodeFieldLabel")}
          value={proof}
          onChange={setProof}
          disabled={busy}
        />
        {action && <DialogError errors={errors} scope={action} />}
      </Stack>
    </Dialog>
  );
}
