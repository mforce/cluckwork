import { useId } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, DialogActions, Stack, TextField, Typography } from "@mui/material";
import { MAX_PASSKEYS, PASSKEY_NAME_MAX } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FarmDate } from "../../components/FarmDate";
import { useFarm } from "../../farm/useFarm";
import { farmDateOf } from "../../lib/relativeTime";
import { Row } from "./SecurityPanel";
import { StepUpDialog } from "./StepUpDialog";
import type { Passkeys } from "./usePasskeys";

// #1283 "Account / Security A": the passkeys under the app in the Security
// accordion, each with its address under its name. While passkeys are off at
// this address the list keeps Remove only (record 1282 "What a caller sees").
export function PasskeysSection({ passkeys }: { passkeys: Passkeys }) {
  const { t } = useTranslation("account");
  const { list, message, openAdd, openRemove, openRename, dialog: { busy } } = passkeys;
  const timeZone = useFarm().farm?.timeZoneId;
  if (list.error !== null) {
    return (
      <Alert severity="error" action={<Button color="inherit" onClick={list.retry}>{t("statusRetry")}</Button>}>
        {t("passkeysLoadFailed")}
      </Alert>
    );
  }
  if (!list.data) return null;
  const { enabled, passkeys: keys } = list.data;
  if (!enabled && keys.length === 0) return null;
  const full = keys.length >= MAX_PASSKEYS;

  return (
    <Box component="section" aria-labelledby="passkeys-heading">
      <Row action={enabled && <Button variant="outlined" disabled={busy || full} onClick={openAdd}>{t("addPasskeyButton")}</Button>}>
        <Typography id="passkeys-heading" variant="h3" component="h3">{t("passkeysHeading")}</Typography>
        <Typography variant="body2" color="text.secondary">{t(enabled ? "passkeysHint" : "passkeysOffHint")}</Typography>
        <Typography variant="caption" color="text.secondary">{t("passkeysCount", { count: keys.length, max: MAX_PASSKEYS })}</Typography>
      </Row>
      <Box role="status" sx={{ "&:empty": { display: "none" }, mb: 1 }}>
        {message && <Alert severity="success" role="none">{message}</Alert>}
      </Box>
      {enabled && full && <Typography variant="caption" component="p" color="text.secondary">{t("passkeysLimit", { max: MAX_PASSKEYS })}</Typography>}
      {keys.some((k) => k.address) && (
        <Typography variant="caption" component="p" color="text.secondary">{t("passkeysMultiAddress")}</Typography>
      )}
      {keys.length === 0 && <Typography color="text.secondary" sx={{ py: 1.5 }}>{t("passkeysEmpty")}</Typography>}
      <Box component="ul" sx={{ listStyle: "none", m: 0, p: 0 }}>
        {keys.map((key) => (
          <li key={key.id}>
            <Row action={(
              <Stack direction="row" spacing={1.5} sx={{ alignItems: "center" }}>
                {enabled && <Button disabled={busy} onClick={() => openRename(key)}>{t("renameButton")}</Button>}
                <Button color="error" disabled={busy} onClick={() => openRemove(key)}>{t("removeButton")}</Button>
              </Stack>
            )}>
              <Typography variant="h3" component="h4" sx={{ overflowWrap: "anywhere" }}>{key.name ?? t("passkeyDefaultName")}</Typography>
              <Typography variant="caption" color="text.secondary">
                {t("passkeyCreatedLabel")} <FarmDate iso={farmDateOf(key.createdAt, timeZone)} />
                {key.synced && ` · ${t("passkeySynced")}`}
              </Typography>
              {key.address && <Typography variant="caption" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{key.address}</Typography>}
            </Row>
          </li>
        ))}
      </Box>
    </Box>
  );
}

const COPY = {
  add: { title: "addPasskeyTitle", body: "addPasskeyBody", submit: "continueButton" },
  remove: { title: "removePasskeyTitle", body: "removePasskeyBody", submit: "removePasskeyButton" },
} as const;

// Add's and Remove's step-up. The page renders it outside the list and the
// create step, as ProofDialog is, since the create step reopens Add.
export function PasskeyStepUpDialog({ passkeys }: { passkeys: Passkeys }) {
  const { t } = useTranslation("account");
  const { stepUp, removing, onStepUp, dialog } = passkeys;
  const remove = stepUp.action === "remove";
  return (
    <StepUpDialog stepUp={stepUp} dialog={dialog} danger={remove} onSubmit={onStepUp} copy={(action) => COPY[action]}
      target={remove && (removing?.name ?? t("passkeyDefaultName"))}>
      {remove && <Alert severity="warning" role="note">{t("passkeySessionWarning")}</Alert>}
    </StepUpDialog>
  );
}

export function RenamePasskeyDialog({ passkeys }: { passkeys: Passkeys }) {
  const bodyId = useId();
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { renaming, newName, setNewName, closeRename, onRename, dialog: { busy, isPending, errors } } = passkeys;
  const blank = !newName.trim();
  return (
    <Dialog
      open={renaming !== null}
      title={t("renamePasskeyTitle")}
      onClose={closeRename}
      closeDisabled={busy}
      describedBy={bodyId}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeRename} disabled={busy}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" disabled={busy || blank} busy={isPending("rename")}>{t("saveNameButton")}</BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onRename }}
    >
      <Stack spacing={2}>
        <Typography id={bodyId} sx={{ lineHeight: 1.55 }}>{t("renamePasskeyBody")}</Typography>
        <TextField label={t("passkeyNameLabel")} value={newName} required disabled={busy}
          error={blank} helperText={blank && t("passkeyNameRequired")}
          onChange={(e) => setNewName(e.target.value)} slotProps={{ htmlInput: { maxLength: PASSKEY_NAME_MAX } }} />
        <DialogError errors={errors} scope="rename" />
      </Stack>
    </Dialog>
  );
}
