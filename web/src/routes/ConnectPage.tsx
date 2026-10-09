import { useEffect, useRef, useState } from "react";
import type { FormEvent, ReactNode, Ref } from "react";
import { useLocation, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Eye, Laptop, PenLine, UserRound } from "lucide-react";
import { Alert, Box, Button, Paper, Stack, Typography } from "@mui/material";
import { ApiError, stepUp } from "../api/client";
import { approveConsent, askConsent, declineConsent } from "../api/oauth";
import type { AuthorizeAnswer, ConsentRequest } from "../api/oauth";
import { useAuth } from "../auth/useAuth";
import { currentUserRole } from "../auth/claims";
import { BusyButton } from "../components/BusyButton";
import { StepUpPasswordField } from "../components/StepUpPasswordField";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm } from "../farm/useFarm";
import { roleLabel } from "../i18n/enums";
import { useMe } from "../session/SessionContext";

const READ = "farm:read";
const WRITE = "daily-entries:write";
const LOOPBACK = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);
// The roles whose policy lets them record daily entries (ProductionWrite).
const RECORDS_ENTRIES = new Set(["Admin", "Manager", "Worker"]);

type View =
  | { kind: "loading" }
  | { kind: "refused" }
  | { kind: "off" }
  | { kind: "leaving" }
  | { kind: "ask"; request: ConsentRequest };

// #798 — the consent screen a connected app sends the user to (compact
// direction D, docs/designs/788-oauth-screens/SELECTION.md). Every Allow spends
// a step-up grant; an app already approved for everything it asks skips the
// permission rows and asks for the password only.
export function ConnectPage() {
  const { t } = useTranslation("connect");
  const { search } = useLocation();
  const navigate = useNavigate();
  const { logout } = useAuth();
  const me = useMe();
  const { farm } = useFarm();
  const [view, setView] = useState<View>({ kind: "loading" });
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const { busy, run } = usePendingAction();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const passwordRef = useRef<HTMLInputElement>(null);
  const wrongPassword = error === t("wrongPassword");

  // A screen reader starts at the request, and a phone keeps its keyboard closed until
  // the user reaches the password. Only a wrong password sends focus back to the field.
  useEffect(() => {
    if (view.kind === "ask") headingRef.current?.focus();
  }, [view.kind]);
  useEffect(() => {
    if (wrongPassword && !busy) passwordRef.current?.focus();
  }, [wrongPassword, busy]);

  function follow(answer: AuthorizeAnswer) {
    if ("redirectUri" in answer) {
      setView({ kind: "leaving" });
      window.location.assign(answer.redirectUri);
    } else {
      setView({ kind: "ask", request: answer });
    }
  }

  useEffect(() => {
    let live = true;
    askConsent(search).then(
      (answer) => { if (live) follow(answer); },
      (err) => { if (live) setView({ kind: isOff(err) ? "off" : "refused" }); });
    return () => { live = false; };
  }, [search]);

  async function allow(e: FormEvent) {
    e.preventDefault();
    await run("allow", async () => {
      setError(null);
      const entered = password;
      setPassword("");
      let grant: string;
      try {
        grant = (await stepUp(entered)).token;
      } catch (err) {
        setError(err instanceof ApiError && err.status === 400 ? t("wrongPassword")
          : err instanceof ApiError && err.status === 429 ? t("tooManyAttempts") : t("failed"));
        return;
      }
      try {
        follow(await approveConsent(search, grant));
      } catch (err) {
        if (isOff(err)) setView({ kind: "off" });
        else setError(t("failed"));
      }
    });
  }

  async function cancel() {
    await run("cancel", async () => {
      setError(null);
      try {
        follow(await declineConsent(search));
      } catch {
        setError(t("failed"));
      }
    });
  }

  const email = me?.email ?? "";

  return (
    <Box component="main" sx={{ minHeight: "100dvh", background: "var(--surface-2)", padding: { xs: "14px", md: "32px" } }}>
      <Box sx={{ maxWidth: 432, marginInline: "auto" }}>
        <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "baseline", flexWrap: "wrap", columnGap: 2, marginBottom: 1.5 }}>
          <Typography variant="body2" sx={{ fontWeight: 600, color: "var(--brand)" }}>{farm?.name}</Typography>
          {/* body1 sets tabular figures, which space an address's hyphens like gaps. */}
          <Typography variant="body1" sx={{ fontWeight: 600, color: "var(--ink)", overflowWrap: "anywhere", fontVariantNumeric: "normal" }}>
            <span className="sr-only">{t("signedInAs")} </span>{email}
          </Typography>
        </Stack>
        <Paper elevation={0} sx={{ padding: { xs: "20px", md: "24px" }, border: "1px solid var(--hairline)", borderRadius: "var(--r-panel)" }}>
          {view.kind === "loading" && <Typography role="status">{t("loading")}</Typography>}
          {view.kind === "leaving" && <Typography role="status">{t("leaving")}</Typography>}
          {view.kind === "refused" && (
            <Stack spacing={2}>
              <Typography variant="h2" component="h1">{t("refusedTitle")}</Typography>
              <Typography variant="body2">{t("refusedBody")}</Typography>
              <Button variant="contained" onClick={() => navigate("/")}>{t("refusedButton")}</Button>
            </Stack>
          )}
          {view.kind === "off" && (
            <Stack spacing={2}>
              <Typography variant="h2" component="h1">{t("offTitle")}</Typography>
              <Typography variant="body2">{t("offBody")}</Typography>
              <Box aria-live="assertive">{error && <Alert severity="error" role="alert">{error}</Alert>}</Box>
              <Button variant="outlined" onClick={() => void cancel()} disabled={busy}>{t("cancel")}</Button>
            </Stack>
          )}
          {view.kind === "ask" && (
            <Stack component="form" spacing={2} onSubmit={allow}>
              <Headline request={view.request} ref={headingRef} />
              {!view.request.alreadyApproved && <Permissions request={view.request} />}
              {view.request.alreadyApproved && <Typography variant="body2">{t("reconnectBody")}</Typography>}
              <StepUpPasswordField label={t("password")} value={password} onChange={setPassword} inputRef={passwordRef} disabled={busy} />
              <Box aria-live="assertive">{error && <Alert severity="error" role="alert">{error}</Alert>}</Box>
              <Stack direction="row" spacing={1}>
                <BusyButton variant="contained" type="submit" busy={busy} sx={{ flex: 1 }}>{t("allow")}</BusyButton>
                <Button variant="outlined" onClick={() => void cancel()} disabled={busy} sx={{ flex: 1 }}>{t("cancel")}</Button>
              </Stack>
              <Typography variant="caption" color="text.secondary">{t("undo")}</Typography>
              {!view.request.alreadyApproved && (
                <Details request={view.request} email={email} onSignOut={() => void logout()} />
              )}
            </Stack>
          )}
        </Paper>
      </Box>
    </Box>
  );
}

const isOff = (err: unknown) => err instanceof ApiError && err.title === "Auth.ConnectedAppsOff";

function Headline({ request, ref }: { request: ConsentRequest; ref: Ref<HTMLHeadingElement> }) {
  const { t } = useTranslation("connect");
  const app = request.clientName ?? t("unnamedApp");
  const wider = !request.alreadyApproved && request.alreadyAllowed.length > 0;
  const title = request.alreadyApproved ? "titleReconnect" : wider ? "titleMore" : "title";
  return (
    <Stack spacing={1} sx={{ alignItems: "flex-start" }}>
      <Typography ref={ref} tabIndex={-1} variant="h2" component="h1" sx={{ overflowWrap: "anywhere", "&:focus": { outline: "none" } }}>
        {t(title, { app })}
      </Typography>
      <span className="badge badge-warn">{t("unverified")}</span>
      {!request.alreadyApproved && (
        <Line icon={<Laptop size={16} aria-hidden />}>
          {LOOPBACK.has(request.redirectHost) ? t("returnsHere") : t("returnsTo", { host: request.redirectHost })}
        </Line>
      )}
    </Stack>
  );
}

function Line({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <Stack direction="row" spacing={0.75} sx={{ alignItems: "center", color: "text.secondary" }}>
      {icon}<Typography variant="body2" color="inherit">{children}</Typography>
    </Stack>
  );
}

function Permissions({ request }: { request: ConsentRequest }) {
  const { t } = useTranslation("connect");
  const had = (scope: string) => request.alreadyAllowed.includes(scope);
  // A request for more puts what is new first; what was allowed before drops to a muted line.
  const scopes = [...request.scopes].sort((a, b) => Number(had(a)) - Number(had(b)));
  const role = currentUserRole();
  const canRecord = RECORDS_ENTRIES.has(role);
  return (
    <>
      <Box component="ul" aria-label={t("scopesLabel")} sx={{ listStyle: "none", margin: 0, padding: 0, borderTop: "1px solid var(--hairline)" }}>
        {scopes.map((scope) => {
          const old = had(scope);
          const isNew = request.alreadyAllowed.length > 0 && !old;
          const read = scope === READ;
          const blocked = scope === WRITE && !canRecord;
          return (
            <Box component="li" key={scope} sx={{
              display: "flex", gap: 1.5, paddingBlock: 1.25, paddingInline: isNew ? 1 : 0, marginInline: isNew ? -1 : 0,
              borderBottom: "1px solid var(--hairline)", background: isNew ? "var(--tint-accent)" : undefined,
              color: old ? "text.secondary" : "text.primary",
            }}>
              {read ? <Eye size={18} aria-hidden /> : <PenLine size={18} aria-hidden />}
              <Box>
                <Typography variant="body2" sx={{ fontWeight: old ? 400 : 600, color: "inherit" }}>
                  {read ? t("scopeRead") : scope === WRITE ? t("scopeWrite") : scope}
                  {isNew && <> <span className="badge badge-accent">{t("scopeNew")}</span></>}
                </Typography>
                {(old || blocked || read) && (
                  <Typography variant="body2" sx={{ color: "text.secondary" }}>
                    {old ? t("scopeHad")
                      : blocked ? <span className="badge badge-warn">{t("scopeBlocked")}</span> : t("scopeReadLine")}
                  </Typography>
                )}
              </Box>
            </Box>
          );
        })}
      </Box>
      <Stack direction="row" sx={{ alignItems: "center", flexWrap: "wrap", gap: 1 }}>
        <UserRound size={18} aria-hidden />
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{t("acts")}</Typography>
        <Box component="span" sx={{
          display: "inline-flex", alignItems: "center", gap: 0.5, padding: "1px 10px",
          border: "1px solid var(--brand)", borderRadius: "var(--r-pill)", color: "var(--brand)",
          fontSize: "0.8rem", fontWeight: 600,
        }}>
          <span className="sr-only">{t("roleSr")} </span>{roleLabel(role)}
        </Box>
      </Stack>
    </>
  );
}

function Details({ request, email, onSignOut }: { request: ConsentRequest; email: string; onSignOut: () => void }) {
  const { t } = useTranslation("connect");
  const app = request.clientName ?? t("unnamedApp");
  const role = currentUserRole();
  const host = request.redirectHost;
  // What this request would get: its scopes, within the user's role and flock scope.
  const flocks = request.assignedFlocks?.join(", ");
  const reads = request.scopes.includes(READ) && (flocks === undefined ? t("detailsReadAll") : t("detailsReadSome", { flocks }));
  const writes = request.scopes.includes(WRITE) && (!RECORDS_ENTRIES.has(role)
    ? t("detailsWriteBlocked", { role: roleLabel(role) })
    : flocks === undefined ? t("detailsWriteAll") : t("detailsWriteSome", { flocks }));
  return (
    <Box component="details" sx={{ borderTop: "1px solid var(--hairline)", paddingTop: 1.5, "& p": { marginBlock: 1 } }}>
      <Box component="summary" sx={{ cursor: "pointer", fontWeight: 600 }}>{t("details")}</Box>
      <Typography variant="body2" component="p"><strong>{t("detailsActs")}</strong> {[reads, writes].filter(Boolean).join(" ")}</Typography>
      <Typography variant="body2" component="p">{t("detailsName", { app })}</Typography>
      <Typography variant="body2" component="p">
        {LOOPBACK.has(host) ? t("detailsReturnHere", { host }) : t("detailsReturnTo", { host })}
      </Typography>
      <Typography variant="body2" component="p">{t("detailsScopes")}</Typography>
      <Typography variant="body2" component="p">
        {email} · <Button variant="text" size="small" onClick={onSignOut} sx={{ padding: 0, minWidth: 0, verticalAlign: "baseline" }}>{t("notYou")}</Button>
      </Typography>
    </Box>
  );
}
