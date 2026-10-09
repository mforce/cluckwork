import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { ChevronDown, Plug } from "lucide-react";
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Stack, TextField, Typography,
} from "@mui/material";
import { changePassword, ApiError } from "../api/client";
import { disconnectMyApp, listMyConnectedApps } from "../api/cluckwork";
import type { AppConnection } from "../api/cluckwork";
import { useConnectionFacts, useDisconnect } from "../components/ConnectedApps";
import { EmptyState } from "../components/EmptyState";
import { PhoneDetailsField } from "../components/PhoneLedger";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { usePendingAction } from "../components/usePendingAction";
import { LanguageSelector } from "../session/LanguageSelector";
import { StepperUnitSelector } from "../session/StepperUnitSelector";
import { SUPPORTED_LANGUAGES } from "../i18n";
import { roleLabel } from "../i18n/enums";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const MIN_LENGTH = 12;

// #165 — the one self-service surface: every role can change their own password
// by proving the current one. Changing it signs out this account's OTHER devices
// (the server revokes every refresh token) while keeping this one signed in.
export function AccountPage() {
  const { t } = useTranslation("account");
  const { role } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const { busy, run } = usePendingAction();

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    // State check first so an Enter-key re-submit mid-flight cannot clear the
    // messages; the hook's ref closes the same-tick window the state misses.
    if (busy) return;
    setError(null);
    setMessage(null);

    // Caught here so a typo never costs a round-trip (the server checks too).
    if (next !== confirm) {
      setError(t("passwordMismatchError"));
      return;
    }
    if (next.length < MIN_LENGTH) {
      setError(t("passwordTooShortError", { min: MIN_LENGTH }));
      return;
    }

    await run("change-password", async () => {
      try {
        // No key threaded here: the server exempts this route from the response
        // cache (#165 review), since replaying it would hand back the access token
        // without the rotated refresh cookie.
        await changePassword({ currentPassword: current, newPassword: next });
        setMessage(t("passwordChangedMessage"));
        setCurrent("");
        setNext("");
        setConfirm("");
      } catch (err) {
        setError(errText(err));
      }
    });
  }

  return (
    <Box component="section" sx={{ maxWidth: "760px" }}>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>
        {t("eyebrow")}
      </Typography>
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        <Trans ns="account" i18nKey="roleLine" values={{ role: roleLabel(role) }} components={{ strong: <strong /> }} />
      </Typography>

      <Accordion defaultExpanded disableGutters>
        <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
          <Typography variant="h3" component="span">{t("preferences")}</Typography>
        </AccordionSummary>
        <AccordionDetails>
          {SUPPORTED_LANGUAGES.length > 1 && (
            <>
              <Typography variant="body2" color="text.secondary">{t("languageHint")}</Typography>
              <LanguageSelector />
            </>
          )}
          {/* #444 — the pack unit YOUR Daily Entry steppers bump by, overriding
              the farm default set in Settings. */}
          <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>{t("stepperUnitHint")}</Typography>
          <StepperUnitSelector />
        </AccordionDetails>
      </Accordion>

      <Accordion defaultExpanded disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
        <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
          <Typography variant="h3" component="span">{t("changePasswordHeading")}</Typography>
        </AccordionSummary>
        <AccordionDetails>
          <Typography variant="body2" color="text.secondary">
            {t("changePasswordHint")}
          </Typography>
          <Stack component="form" spacing={2} sx={{ mt: 2, maxWidth: "24rem" }} onSubmit={onSubmit}>
            <TextField
              label={t("currentPasswordLabel")}
              type="password"
              value={current}
              required
              autoComplete="current-password"
              onChange={(e) => setCurrent(e.target.value)}
              slotProps={{ htmlInput: { maxLength: 256 } }}
            />
            <TextField
              label={t("newPasswordLabel", { min: MIN_LENGTH })}
              type="password"
              value={next}
              required
              autoComplete="new-password"
              onChange={(e) => setNext(e.target.value)}
              slotProps={{ htmlInput: { minLength: MIN_LENGTH, maxLength: 256 } }}
            />
            <TextField
              label={t("confirmPasswordLabel")}
              type="password"
              value={confirm}
              required
              autoComplete="new-password"
              onChange={(e) => setConfirm(e.target.value)}
              slotProps={{ htmlInput: { maxLength: 256 } }}
            />
            {error && <Alert severity="error">{error}</Alert>}
            {message && <Alert severity="success">{message}</Alert>}
            <Stack direction="row">
              <BusyButton variant="contained" type="submit" busy={busy}>{t("changePasswordButton")}</BusyButton>
            </Stack>
          </Stack>
        </AccordionDetails>
      </Accordion>

      <ConnectedAppsPanel />
    </Box>
  );
}

const DISCONNECT_SX = { minHeight: 44, width: { xs: "100%", md: "auto" } };

// #799 — the apps allowed to act as you (#798's consent says to come here to undo
// one). Each row opens to what it can do and when it was connected and last used.
function ConnectedAppsPanel() {
  const { t } = useTranslation("connectedApps");
  const facts = useConnectionFacts();
  const [apps, setApps] = useState<AppConnection[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const load = useCallback(() =>
    listMyConnectedApps().then((rows) => { setApps(rows); setLoadFailed(false); }, () => setLoadFailed(true)), []);
  useEffect(() => { void load(); }, [load]);
  const { disconnect, busy, error, notice, sectionRef, confirmDialog } = useDisconnect(load);
  const ask = (app: AppConnection) => {
    const name = facts.name(app);
    void disconnect(app.clientId, name, t("confirmBody", { app: name }), () => disconnectMyApp(app.clientId));
  };
  const idle = apps?.find(facts.isIdle);

  return (
    <Accordion defaultExpanded disableGutters ref={sectionRef}>
      <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />} data-disconnect-fallback>
        <Typography variant="h3" component="span">
          {t("heading")}{apps && apps.length > 0 && <Box component="span" sx={{ color: "text.secondary", fontWeight: 400 }}> {apps.length}</Box>}
        </Typography>
      </AccordionSummary>
      <AccordionDetails>
        <Typography variant="body2" color="text.secondary">{t("hint")}</Typography>
        {notice}
        <Stack spacing={2} sx={{ mt: 2 }}>
          {loadFailed && <Alert severity="error">{t("loadFailed")}</Alert>}
          {error && <Alert severity="error">{error}</Alert>}
          {apps?.length === 0 && <EmptyState icon={Plug} message={t("empty")} />}
          {apps && apps.length > 0 && <Typography>{t("count", { count: apps.length })}</Typography>}
          {idle && (
            <Alert severity="warning" icon={false} sx={{ flexWrap: { xs: "wrap", md: "nowrap" }, "& .MuiAlert-action": { ml: { xs: 0, md: "auto" }, pl: { xs: 0, md: 2 } } }}
              action={(
                <Button variant="outlined" color="error" disabled={busy} onClick={() => ask(idle)} sx={{ ...DISCONNECT_SX, whiteSpace: { md: "nowrap" } }}>
                  {t("disconnectApp", { app: facts.name(idle) })}
                </Button>
              )}>
              {t("nudge", { app: facts.name(idle), days: facts.idleDays(idle) })}
            </Alert>
          )}
          {apps && apps.length > 0 && (
            <Box sx={{ borderTop: "1px solid var(--rule)" }}>
              {apps.map((app) => (
                <Box component="details" key={app.clientId} sx={{ borderBottom: "1px solid var(--rule)", "&[open] > summary svg": { transform: "rotate(180deg)" } }}>
                  <Box component="summary" tabIndex={0} data-disconnect-focus={app.clientId} sx={{ display: "flex", alignItems: "center", gap: 2, py: 1.5, cursor: "pointer", listStyle: "none", "&::-webkit-details-marker": { display: "none" } }}>
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      <Typography sx={{ fontWeight: 650, overflowWrap: "anywhere" }}>{facts.name(app)}</Typography>
                      {facts.status(app)}
                    </Box>
                    <ChevronDown size={18} aria-hidden />
                  </Box>
                  <Box component="dl" sx={{ m: 0 }}>
                    <PhoneDetailsField label={t("can")}>{facts.can(app)}</PhoneDetailsField>
                    <PhoneDetailsField label={t("connected")}>{facts.connected(app)}</PhoneDetailsField>
                    <PhoneDetailsField label={t("lastUsed")}>{facts.lastUsed(app)}</PhoneDetailsField>
                  </Box>
                  <Button variant="outlined" color="error" disabled={busy} onClick={() => ask(app)}
                    aria-label={t("disconnectApp", { app: facts.name(app) })} sx={{ ...DISCONNECT_SX, my: 1.5 }}>
                    {t("disconnect")}
                  </Button>
                </Box>
              ))}
            </Box>
          )}
        </Stack>
        {confirmDialog}
      </AccordionDetails>
    </Accordion>
  );
}
