import { useId } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, DialogActions, Stack, TextField, Typography } from "@mui/material";
import { MAX_PASSKEYS } from "../../api/cluckwork";
import type { Passkey } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { FarmDate } from "../../components/FarmDate";
import { ConfirmItsYou } from "../../components/StepUpProofFields";
import { useFarm } from "../../farm/useFarm";
import { farmDateOf } from "../../lib/relativeTime";
import type { Passkeys } from "./usePasskeys";

// The server's own limit (PasskeyNameMaxLength).
const NAME_MAX = 64;

// #1283 "Account / Security A": the passkeys under the app in the Security
// accordion, each with its address under its name. While passkeys are off at
// this address the list keeps Remove only (record 1282 "What a caller sees").
export function PasskeysSection({ passkeys }: { passkeys: Passkeys }) {
  const { t } = useTranslation("account");
  const { list, message, openAdd, openFor, dialog: { busy } } = passkeys;
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
    <Box component="section" aria-labelledby="passkeys-heading" sx={{ borderTop: "1px solid var(--rule)", pt: 1.5 }}>
      <Box sx={{ display: "flex", flexWrap: { xs: "wrap", md: "nowrap" }, alignItems: { md: "center" }, gap: 2 }}>
        <Stack spacing={0.5} sx={{ flex: "1 1 14rem", minWidth: 0 }}>
          <Typography id="passkeys-heading" variant="h3" component="h3">{t("passkeysHeading")}</Typography>
          <Typography variant="body2" color="text.secondary">{t(enabled ? "passkeysHint" : "passkeysOffHint")}</Typography>
          <Typography variant="caption" color="text.secondary">{t("passkeysCount", { count: keys.length, max: MAX_PASSKEYS })}</Typography>
        </Stack>
        {enabled && (
          <Button variant="outlined" disabled={busy || full} onClick={openAdd} sx={{ flexShrink: 0 }}>{t("addPasskeyButton")}</Button>
        )}
      </Box>
      <Box role="status" sx={{ "&:empty": { display: "none" }, mt: 1 }}>
        {message && <Alert severity="success" role="none">{message}</Alert>}
      </Box>
      {enabled && full && <Typography variant="caption" component="p" color="text.secondary">{t("passkeysLimit", { max: MAX_PASSKEYS })}</Typography>}
      {keys.some((k) => k.address) && (
        <Typography variant="caption" component="p" color="text.secondary">{t("passkeysMultiAddress")}</Typography>
      )}
      {keys.length === 0 && <Typography color="text.secondary" sx={{ py: 1.5 }}>{t("passkeysEmpty")}</Typography>}
      <Box component="ul" sx={{ listStyle: "none", m: 0, p: 0 }}>
        {keys.map((key) => (
          <Box component="li" key={key.id} sx={{
            display: "flex", flexWrap: { xs: "wrap", md: "nowrap" }, alignItems: { md: "center" }, gap: 2,
            py: 1.5, borderBottom: "1px solid var(--rule)",
          }}>
            <Stack spacing={0.25} sx={{ flex: "1 1 14rem", minWidth: 0 }}>
              <Typography variant="h3" component="h4" sx={{ overflowWrap: "anywhere" }}>{key.name ?? t("passkeyUnnamed")}</Typography>
              <Typography variant="caption" color="text.secondary">
                {t("passkeyCreatedLabel")} <FarmDate iso={farmDateOf(key.createdAt, timeZone)} />
                {key.synced && ` · ${t("passkeySynced")}`}
              </Typography>
              {key.address && <Typography variant="caption" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{key.address}</Typography>}
            </Stack>
            <Stack direction="row" spacing={1.5} sx={{ flexShrink: 0, alignItems: "center" }}>
              {enabled && <Button disabled={busy} onClick={() => openFor("rename", key)}>{t("renameButton")}</Button>}
              <Button color="error" disabled={busy} onClick={() => openFor("remove", key)}>{t("removeButton")}</Button>
            </Stack>
          </Box>
        ))}
      </Box>
    </Box>
  );
}

const COPY = {
  add: { title: "addPasskeyTitle", body: "addPasskeyBody", submit: "continueButton" },
  rename: { title: "renamePasskeyTitle", body: "renamePasskeyBody", submit: "saveNameButton" },
  remove: { title: "removePasskeyTitle", body: "removePasskeyBody", submit: "removePasskeyButton" },
} as const;

// Add's step-up, Rename and Remove. The page renders it outside the list and
// the create step, as ProofDialog is, since the create step reopens Add.
export function PasskeyDialog({ passkeys }: { passkeys: Passkeys }) {
  const bodyId = useId();
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { open, proof, setProof, name, setName, closeDialog, onDialog, dialog: { busy, isPending, errors } } = passkeys;
  const copy = open && COPY[open.kind];
  const target: Passkey | null = open && open.kind !== "add" ? open.passkey : null;
  return (
    <Dialog
      open={open !== null}
      title={copy ? t(copy.title) : ""}
      onClose={closeDialog}
      closeDisabled={busy}
      describedBy={bodyId}
      actions={(
        <DialogActions>
          <button type="button" className="link" onClick={closeDialog} disabled={busy}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" color={open?.kind === "remove" ? "error" : "primary"} disabled={busy}
            busy={open !== null && isPending(open.kind)}>
            {copy && t(copy.submit)}
          </BusyButton>
        </DialogActions>
      )}
      formProps={{ onSubmit: onDialog }}
    >
      <Stack spacing={2}>
        {open?.kind === "remove" && target && (
          <Typography sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>{target.name ?? t("passkeyUnnamed")}</Typography>
        )}
        <Typography id={bodyId} sx={{ lineHeight: 1.55 }}>{copy && t(copy.body)}</Typography>
        {open?.kind === "rename" && (
          <TextField label={t("passkeyNameLabel")} value={name} required disabled={busy}
            onChange={(e) => setName(e.target.value)} slotProps={{ htmlInput: { maxLength: NAME_MAX } }} />
        )}
        {open?.kind === "remove" && <Alert severity="warning" role="note">{t("passkeySessionWarning")}</Alert>}
        {open && open.kind !== "rename" && <ConfirmItsYou value={proof} onChange={setProof} disabled={busy} />}
        {open && <DialogError errors={errors} scope={open.kind} />}
      </Stack>
    </Dialog>
  );
}
