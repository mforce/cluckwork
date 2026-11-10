import { useEffect, useRef } from "react";
import type { RefObject } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Link, Paper, Stack, TextField, Typography } from "@mui/material";
import { BusyButton } from "../../components/BusyButton";
import { DialogError } from "../../components/DialogError";
import { useCopy } from "../../components/useCopy";
import { TwoFactorQr } from "./TwoFactorQr";
import type { Flow, TwoFactor } from "./useTwoFactor";

// #1275 "Enrolment A": the steps that replace the Account page while they run,
// so the QR code and the key leave the screen with the step that shows them.
export function TwoFactorFlow({ twoFactor }: { twoFactor: TwoFactor }) {
  const { t } = useTranslation("account");
  const { flow } = twoFactor;
  // #1281 — the control that opened this step has left the page with the
  // summary, so each step's heading takes focus.
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    headingRef.current?.focus();
  }, [flow.step]);
  return (
    <Box component="section" sx={{ maxWidth: "760px" }}>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>{t("flowEyebrow")}</Typography>
      <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 }, mt: 1 }}>
        {flow.step === "scan" && <ScanStep twoFactor={twoFactor} flow={flow} headingRef={headingRef} />}
        {flow.step === "restart" && <RestartStep twoFactor={twoFactor} flow={flow} headingRef={headingRef} />}
      </Paper>
    </Box>
  );
}

const heading = (replacing: boolean) => (replacing ? "replaceHeading" : "enrolHeading");

const RESTART_REASONS = { expired: "setUpExpired", attempts: "setUpTooManyAttempts", session: "setUpSessionChanged" } as const;

function ScanStep({ twoFactor, flow, headingRef }: {
  twoFactor: TwoFactor; flow: Extract<Flow, { step: "scan" }>; headingRef: RefObject<HTMLHeadingElement | null>;
}) {
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { code, setCode, onConfirm, leaveFlow, dialog: { busy, isPending, errors } } = twoFactor;
  const { key, otpauthUri } = flow.enrolment;
  const { copy, notice } = useCopy();
  return (
    <Stack spacing={2} component="form" onSubmit={onConfirm}>
      <Typography variant="h2" ref={headingRef} tabIndex={-1}>{t(heading(flow.replacing))}</Typography>
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

function RestartStep({ twoFactor, flow, headingRef }: {
  twoFactor: TwoFactor; flow: Extract<Flow, { step: "restart" }>; headingRef: RefObject<HTMLHeadingElement | null>;
}) {
  const { t } = useTranslation("account");
  const { t: tc } = useTranslation("common");
  const { openProof, leaveFlow } = twoFactor;
  return (
    <Stack spacing={2}>
      <Typography variant="h2" ref={headingRef} tabIndex={-1}>{t(heading(flow.replacing))}</Typography>
      <Alert severity="error">{t(RESTART_REASONS[flow.reason])}</Alert>
      <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
        <Button variant="contained" onClick={() => openProof("start")}>{t("startAgainButton")}</Button>
        <button type="button" className="link" onClick={leaveFlow}>{tc("cancel")}</button>
      </Stack>
    </Stack>
  );
}
