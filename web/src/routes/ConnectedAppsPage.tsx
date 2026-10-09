import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Plug, Unplug } from "lucide-react";
import {
  Alert, Box, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography, useMediaQuery,
} from "@mui/material";
import { disconnectUserApp, getAccount, listFarmConnectedApps, listUsers } from "../api/cluckwork";
import type { AppConnection, User } from "../api/cluckwork";
import { useConnectionFacts, useDisconnect } from "../components/ConnectedApps";
import { EmptyState } from "../components/EmptyState";
import { CONSOLE_DESTRUCTIVE_LINK_SX, LedgerTableContainer, STICKY_TABLE_HEAD_SX } from "../components/FieldConsole";
import { PhoneLedgerList } from "../components/PhoneLedger";
import { roleLabel } from "../i18n/enums";
import { MD_UP_QUERY } from "../lib/breakpoints";

const MUTED = { color: "text.secondary", fontSize: ".8125rem" };

// #799 — every app anyone on the farm has connected, for its Owner. Revoking one is
// less than the Owner can already do (reset a password, disable the person), and it
// keeps a departed employee's app from staying connected with nobody able to see it.
export function ConnectedAppsPage() {
  const { t } = useTranslation("connectedApps");
  const facts = useConnectionFacts();
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const [apps, setApps] = useState<AppConnection[] | null>(null);
  const [users, setUsers] = useState<User[]>([]);
  const [loadFailed, setLoadFailed] = useState(false);
  // #1146 — read with the rows, not from the session's farm: another Owner may have flipped it.
  const [appsOff, setAppsOff] = useState(false);
  const [person, setPerson] = useState("");
  const load = useCallback(() =>
    Promise.all([listFarmConnectedApps(), listUsers(), getAccount()]).then(([rows, people, account]) => {
      setApps(rows);
      setUsers(people);
      setAppsOff(!account.allowConnectedApps);
      setLoadFailed(false);
    }, () => setLoadFailed(true)), []);
  useEffect(() => { void load(); }, [load]);
  const { disconnect, busy, error, notice, sectionRef, confirmDialog } = useDisconnect(load);

  const byId = new Map(users.map((user) => [user.id, user]));
  const who = (userId: string) => byId.get(userId)?.displayName ?? byId.get(userId)?.email ?? userId;
  const sorted = [...(apps ?? [])].sort((a, b) =>
    who(a.userId).localeCompare(who(b.userId)) || facts.name(a).localeCompare(facts.name(b)));
  const people = [...new Set(sorted.map((app) => app.userId))];
  // A person whose last app was just disconnected drops out of the filter: show everyone.
  const selected = people.includes(person) ? person : "";
  const shown = sorted.filter((app) => selected === "" || app.userId === selected);
  const key = (app: AppConnection) => `${app.userId}/${app.clientId}`;
  const ask = (app: AppConnection) => {
    const name = facts.name(app);
    void disconnect(key(app), name, t("confirmBodyOwner", { app: name, person: who(app.userId) }),
      () => disconnectUserApp(app.userId, app.clientId));
  };
  const role = (app: AppConnection) => {
    const user = byId.get(app.userId);
    return user ? roleLabel(user.role) : "";
  };

  return (
    <Box component="section" ref={sectionRef} sx={{ maxWidth: "1000px" }}>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>{t("farmEyebrow")}</Typography>
      <Typography variant="h2" tabIndex={-1} data-disconnect-fallback>{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ borderLeft: "3px solid var(--rule)", pl: 1.5, my: 2, maxWidth: "38rem" }}>
        {t("farmHint")}
      </Typography>

      {notice}
      <Stack spacing={2} sx={{ mt: 2 }}>
        {appsOff && <Alert severity="warning">{t("farmOff")}</Alert>}
        {loadFailed && <Alert severity="error">{t("loadFailed")}</Alert>}
        {error && <Alert severity="error">{error}</Alert>}
        {apps?.length === 0 && <EmptyState icon={Plug} message={t("farmEmpty")} />}
        {apps && apps.length > 0 && (
          <Stack direction={{ xs: "column", md: "row" }} spacing={{ xs: 1, md: 2 }} sx={{ alignItems: { md: "center" } }}>
            <TextField select label={t("person")} value={selected} onChange={(e) => setPerson(e.target.value)}
              slotProps={{ select: { native: true }, inputLabel: { shrink: true } }} sx={{ minWidth: { md: "14rem" } }}>
              <option value="">{t("everyone")}</option>
              {people.map((id) => <option key={id} value={id}>{who(id)}</option>)}
            </TextField>
            <Typography sx={MUTED}>
              {t("farmCount", {
                connections: t("connections", { count: shown.length }),
                people: t("people", { count: new Set(shown.map((app) => app.userId)).size }),
              })}
            </Typography>
          </Stack>
        )}

        {shown.length > 0 && (isDesktop ? (
          <LedgerTableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("person")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("app")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("connected")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("lastUsed")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}><span className="sr-only">{t("disconnect")}</span></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {shown.map((app) => (
                  <TableRow key={key(app)}>
                    <TableCell>{who(app.userId)}<Box sx={MUTED}>{role(app)}</Box></TableCell>
                    <TableCell sx={{ overflowWrap: "anywhere" }}>{facts.name(app)}<Box sx={MUTED}>{facts.can(app)}</Box></TableCell>
                    <TableCell sx={{ whiteSpace: "nowrap" }}>{facts.connected(app)}</TableCell>
                    <TableCell>{facts.lastUsed(app)}{facts.isIdle(app) && <Box>{facts.status(app)}</Box>}</TableCell>
                    <TableCell align="right">
                      <Button variant="text" sx={CONSOLE_DESTRUCTIVE_LINK_SX} disabled={busy} onClick={() => ask(app)}
                        startIcon={<Unplug size={14} aria-hidden />} data-disconnect-focus={key(app)}
                        aria-label={t("disconnectPersonApp", { app: facts.name(app), person: who(app.userId) })}>
                        {t("disconnect")}
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </LedgerTableContainer>
        ) : (
          <PhoneLedgerList label={t("heading")}>
            {shown.map((app) => (
              <Box component="li" key={key(app)} sx={{ px: 1.5, py: 1.5 }}>
                <Typography sx={{ fontWeight: 650 }}>
                  {who(app.userId)} <Box component="span" sx={{ ...MUTED, fontWeight: 400 }}>· {role(app)}</Box>
                </Typography>
                <Typography sx={{ overflowWrap: "anywhere" }}>{facts.name(app)}</Typography>
                <Typography sx={MUTED}>{t("can")}: {facts.can(app)}</Typography>
                <Typography sx={MUTED}>{t("connected")} {facts.connected(app)} · {t("lastUsed")} {facts.lastUsed(app)}</Typography>
                {facts.isIdle(app) && facts.status(app)}
                <Button variant="outlined" color="error" fullWidth disabled={busy} onClick={() => ask(app)} sx={{ minHeight: 44, mt: 1 }}
                  data-disconnect-focus={key(app)}
                  aria-label={t("disconnectPersonApp", { app: facts.name(app), person: who(app.userId) })}>
                  {t("disconnect")}
                </Button>
              </Box>
            ))}
          </PhoneLedgerList>
        ))}
      </Stack>
      {confirmDialog}
    </Box>
  );
}
