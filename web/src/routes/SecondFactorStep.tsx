import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Stack, Typography } from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ApiError, startSecondFactorPasskey } from "../api/client";
import type { SecondFactor } from "../api/client";
import { FARM_SUSPENDED } from "../api/session";
import { getPasskey, passkeysSupported, webAuthnHost } from "../auth/passkey";
import { BusyButton } from "../components/BusyButton";
import { SecondFactorCodeField } from "../components/SecondFactorCodeField";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { secondFactorMessage } from "../lib/secondFactorMessage";
import { messageFor, PASSKEYS_OFF } from "./signInMessage";

// #1274 "What a caller sees": expired and suspended send the user back to the
// password step; every other refusal keeps them here.
function codeStepMessage(err: unknown): { message: string; restart: boolean } | null {
  if (!(err instanceof ApiError)) return null;
  if (err.title === FARM_SUSPENDED) return { message: i18n.t("auth:farmSuspended"), restart: true };
  if (err.status === 401) return { message: i18n.t("auth:secondFactorExpired"), restart: true };
  const message = secondFactorMessage(err);
  return message === null ? null : { message, restart: false };
}

interface Props {
  farmCode: string;
  // The email typed at the password step, shown above the prompt.
  email: string;
  // #1274 — held in the parent's state only, never in storage or a URL.
  challenge: string;
  // #1285 — what the pending 401's secondFactors offers this user.
  factors: SecondFactor[];
  // Back to the password step, with the message to show there, if any.
  onRestart: (message: string | null) => void;
}

// A completed sign-in flips isAuthenticated, and the Login page's effect then
// takes the user to where they were going.
export function SecondFactorStep({ farmCode, email, challenge, factors, onRestart }: Props) {
  const { t } = useTranslation("auth");
  const { completeSecondFactor } = useAuth();
  const { busy, run } = usePendingAction();
  const codeOffered = factors.includes("authenticator");
  // Learned from this step's own options route (record 1282 decision 5), or
  // from an IP host, where that route can only answer 404 Passkeys.Off.
  const [passkeysOff, setPasskeysOff] = useState(() => !webAuthnHost(window.location.hostname));
  const passkeyUsable = passkeysSupported() && !passkeysOff;
  // #1283 "Sign-in A": the passkey first when it can be used, or when it is
  // the user's only second factor.
  const [usePasskey, setUsePasskey] = useState(factors.includes("passkey") && (passkeyUsable || !codeOffered));
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);

  // unavailable: the user would answer with a passkey, but this browser
  // cannot, or the address has passkeys off.
  const view = !usePasskey ? "code" : passkeyUsable ? "passkey" : "unavailable";
  const other = view === "code"
    ? (factors.includes("passkey") && passkeyUsable ? "passkey" : null)
    : (codeOffered ? "code" : null);
  const unavailable = !passkeysSupported() ? t("passkeySecondUnsupported")
    : codeOffered ? i18n.t("errors:Passkeys.Off") : t("passkeySecondOff");

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    await run("secondFactor", async () => {
      setError(null);
      try {
        const given = view === "passkey" ? await getPasskey(await startSecondFactorPasskey(challenge)) : code;
        if (given === null) {
          setError(t("passkeySecondCancelled"));
          return;
        }
        await completeSecondFactor(farmCode, challenge, given);
      } catch (err) {
        if (err instanceof ApiError && err.title === PASSKEYS_OFF) {
          setPasskeysOff(true);
          return;
        }
        const message = codeStepMessage(err);
        if (message?.restart) onRestart(message.message);
        else setError(message?.message ?? messageFor(err));
      }
    });
  }

  return (
    <Stack component="form" spacing={2} onSubmit={onSubmit}>
      <Typography variant="h2">{view === "code" ? t("secondFactorTitle") : t("passkeySecondTitle")}</Typography>
      {/* #1283 "Sign-in A": who is signing in, and where, in every view. The
          page holds the farm code, not the farm's name. */}
      <Stack spacing={0.5}>
        <Typography variant="body2" sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>{email}</Typography>
        <Typography variant="caption" color="text.secondary">
          {t("passkeySecondConfirmed", { farm: farmCode.trim() })}
        </Typography>
      </Stack>
      {view !== "unavailable" && (
        <Typography variant="body2" color="text.secondary">
          {view === "code" ? t("secondFactorIntro") : t("passkeySecondIntro")}
        </Typography>
      )}
      {view === "code" && <SecondFactorCodeField id="one-time-code" value={code} onChange={setCode} autoFocus />}
      {view === "unavailable" && <Alert severity="warning">{unavailable}</Alert>}
      {error && <Alert severity="error">{error}</Alert>}
      {view !== "unavailable" && (
        <BusyButton variant="contained" type="submit" busy={busy}>
          {view === "passkey" ? t("passkeySecondSubmit") : busy ? t("secondFactorVerifying") : t("secondFactorSubmit")}
        </BusyButton>
      )}
      {other !== null && (
        <Button
          variant={other === "code" ? "outlined" : "text"}
          type="button"
          disabled={busy}
          onClick={() => {
            setError(null);
            setUsePasskey(other === "passkey");
          }}
        >
          {other === "code" ? t("passkeySecondUseCode") : t("passkeySecondSubmit")}
        </Button>
      )}
      {view === "passkey" && !codeOffered && (
        <Typography variant="body2" color="text.secondary">{t("passkeySecondOnly")}</Typography>
      )}
      <Button variant="text" type="button" disabled={busy} onClick={() => onRestart(null)}>
        {t("secondFactorBack")}
      </Button>
    </Stack>
  );
}
