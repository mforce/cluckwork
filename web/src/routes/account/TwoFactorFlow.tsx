import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Checkbox, FormControlLabel, Link, Paper, Stack, TextField, Typography } from "@mui/material";
import { BusyButton } from "../../components/BusyButton";
import { DialogError } from "../../components/DialogError";
import { saveBlob } from "../../lib/saveBlob";
import { useMe } from "../../session/SessionContext";
import { TwoFactorQr } from "./TwoFactorQr";
import type { Flow, TwoFactor } from "./useTwoFactor";

// #1275 "Enrolment A" and "Recovery codes": the steps that replace the Account
// page while they run, so the QR code, the key and the codes leave the screen
// with the step that shows them.
export function TwoFactorFlow({ twoFactor }: { twoFactor: TwoFactor }) {
  const { t } = useTranslation("account");
  const { flow } = twoFactor;
  return (
    <Box component="section" sx={{ maxWidth: "760px" }}>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>{t("flowEyebrow")}</Typography>
      <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 }, mt: 1 }}>
        {flow.step === "scan" && <ScanStep twoFactor={twoFactor} flow={flow} />}
        {flow.step === "restart" && <RestartStep twoFactor={twoFactor} flow={flow} />}
        {flow.step === "codes" && <CodesStep twoFactor={twoFactor} flow={flow} />}
      </Paper>
    </Box>
  );
}

const heading = (replacing: boolean) => (replacing ? "replaceHeading" : "enrolHeading");

// Copy feedback, keyed so a second press announces again.
function useCopy() {
  const [note, setNote] = useState<{ text: string; ok: boolean } | null>(null);
  // Started inside the promise: a plain-http origin has no clipboard at all.
  const copy = (text: string, done: string, failed: string) =>
    Promise.resolve().then(() => navigator.clipboard.writeText(text))
      .then(() => setNote({ text: done, ok: true }), () => setNote({ text: failed, ok: false }));
  const notice: ReactNode = (
    <Box role="status" sx={{ "&:empty": { display: "none" } }}>
      {note && <Alert severity={note.ok ? "success" : "warning"} role="none">{note.text}</Alert>}
    </Box>
  );
  return { copy, notice, setNote };
}

function ScanStep({ twoFactor, flow }: { twoFactor: TwoFactor; flow: Extract<Flow, { step: "scan" }> }) {
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { code, setCode, onConfirm, leaveFlow, dialog: { busy, isPending, errors } } = twoFactor;
  const { key, otpauthUri } = flow.enrolment;
  const { copy, notice } = useCopy();
  return (
    <Stack spacing={2} component="form" onSubmit={onConfirm}>
      <Typography variant="h2">{t(heading(flow.replacing))}</Typography>
      <Box sx={{ display: "flex", flexWrap: { xs: "wrap", md: "nowrap" }, gap: 3, alignItems: "flex-start" }}>
        <TwoFactorQr uri={otpauthUri} label={t("qrLabel")} />
        <Stack spacing={1.5} sx={{ flex: 1, minWidth: { xs: "100%", md: 0 } }}>
          <Typography variant="h3" component="h3">{t("scanHeading")}</Typography>
          <Typography color="text.secondary">{t("scanHint")}</Typography>
          {/* A phone cannot scan its own screen; the link hands the URI to its app. */}
          <Link href={otpauthUri} sx={{ display: { xs: "inline", md: "none" }, minHeight: 44 }}>{t("openInAppLink")}</Link>
          <Box component="details" sx={{ "& > summary": { cursor: "pointer", py: 1.25 } }}>
            <summary>{t("cannotScan")}</summary>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <Typography color="text.secondary">{t("keyHint")}</Typography>
              <Box component="code" sx={{ fontFamily: "monospace", fontSize: "1.05rem", letterSpacing: "0.08em", overflowWrap: "anywhere" }}>
                {key.match(/.{1,4}/g)?.join(" ")}
              </Box>
              <Box><Button variant="outlined" onClick={() => void copy(key, t("keyCopied"), t("copyFailed"))}>{t("copyKeyButton")}</Button></Box>
              {notice}
            </Stack>
          </Box>
        </Stack>
      </Box>
      <Stack spacing={2} sx={{ borderTop: "1px solid var(--rule)", pt: 2, maxWidth: "24rem" }}>
        <Typography variant="h3" component="h3">{t("confirmHeading")}</Typography>
        <Typography color="text.secondary">{t("confirmHint")}</Typography>
        <TextField
          label={t("codeLabel")}
          value={code}
          required
          disabled={busy}
          onChange={(e) => setCode(e.target.value)}
          slotProps={{ htmlInput: { inputMode: "numeric", autoComplete: "one-time-code", maxLength: 8 } }}
        />
        <Alert severity="warning" role="note">{t("signOutWarning")}</Alert>
        <DialogError errors={errors} scope="confirm" />
      </Stack>
      <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
        <BusyButton variant="contained" type="submit" busy={isPending("confirm")} disabled={busy}>{t("confirmButton")}</BusyButton>
        <button type="button" className="link" onClick={leaveFlow} disabled={busy}>{tc("cancel")}</button>
      </Stack>
    </Stack>
  );
}

function RestartStep({ twoFactor, flow }: { twoFactor: TwoFactor; flow: Extract<Flow, { step: "restart" }> }) {
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { openProof, leaveFlow } = twoFactor;
  return (
    <Stack spacing={2}>
      <Typography variant="h2">{t(heading(flow.replacing))}</Typography>
      <Alert severity="error">{t(flow.reason === "expired" ? "setUpExpired" : "setUpTooManyAttempts")}</Alert>
      <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
        <Button variant="contained" onClick={() => openProof("start")}>{t("startAgainButton")}</Button>
        <button type="button" className="link" onClick={leaveFlow}>{tc("cancel")}</button>
      </Stack>
    </Stack>
  );
}

// Shown once: the codes are in this step's props and nowhere else, and Done
// drops them. Closing the tab first asks the browser to confirm.
function CodesStep({ twoFactor, flow }: { twoFactor: TwoFactor; flow: Extract<Flow, { step: "codes" }> }) {
  const { t } = useTranslation("account");
  const me = useMe();
  const { copy, notice, setNote } = useCopy();
  const [saved, setSaved] = useState(false);
  const text = flow.codes.join("\n");

  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, []);

  const download = () => {
    saveBlob(new Blob([`${t("codesFileTitle", { email: me?.email ?? "" })}\n\n${text}\n`], { type: "text/plain" }), "cluckwork-recovery-codes.txt");
    setNote({ text: t("codesDownloaded"), ok: true });
  };

  return (
    <Stack spacing={2}>
      <Typography variant="h2">{t("codesHeading")}</Typography>
      <Typography>{t("codesOn")}</Typography>
      {flow.regenerated && <Alert severity="warning" role="note">{t("codesReplaced")}</Alert>}
      <Typography color="text.secondary">{t("codesHint")}</Typography>
      <Box component="ul" aria-label={t("codesListLabel")} sx={{
        listStyle: "none", m: 0, p: 2, display: "grid", gap: 1, gridTemplateColumns: { xs: "1fr", sm: "1fr 1fr" },
        border: "1px solid var(--rule)", borderRadius: 1, fontFamily: "monospace", fontSize: "1.05rem",
      }}>
        {flow.codes.map((c) => <li key={c}>{c}</li>)}
      </Box>
      <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5}>
        <Button variant="outlined" onClick={() => void copy(text, t("codesCopied"), t("copyFailed"))}>{t("copyCodesButton")}</Button>
        <Button variant="outlined" onClick={download}>{t("downloadButton")}</Button>
      </Stack>
      {notice}
      <FormControlLabel control={<Checkbox checked={saved} onChange={(e) => setSaved(e.target.checked)} />} label={t("savedCheckbox")} />
      <Box><Button variant="contained" disabled={!saved} onClick={twoFactor.leaveFlow}>{t("doneButton")}</Button></Box>
    </Stack>
  );
}
