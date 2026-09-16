import { Fragment, Suspense, useEffect } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router";
import { LogOut } from "lucide-react";
import { useTranslation } from "react-i18next";
import {
  Alert, Box, Button, Drawer, List, ListItemButton, ListItemIcon, ListItemText, ListSubheader, Typography,
} from "@mui/material";
import { useAuth } from "../auth/useAuth";
import { ThemeToggle } from "../components/ThemeToggle";
import { BottomNav } from "../components/BottomNav";
import { useMissedAnnouncement } from "../components/useMissedAnnouncement";
import { ErrorBoundary } from "../components/ErrorBoundary";
import { FarmBrand } from "../components/FarmBrand";
import { useFarm } from "../farm/useFarm";
import { navGroups, tabEntries } from "./nav";
import type { NavEntry } from "./nav";

const ICON = 17;

// #458 — same env var, same "absent in dev" contract errorReport.ts already
// relies on for crash reports; read once at module scope rather than per render.
const APP_VERSION = import.meta.env.VITE_APP_VERSION as string | undefined;

// A route matches an entry the same way everywhere in the shell: exact for an
// `end` entry (only the Dashboard today), a prefix match otherwise. BottomNav
// needs the identical test for its own current-tab check, so this is the one
// place both renderers would otherwise duplicate it from.
function matches(pathname: string, entry: NavEntry): boolean {
  return entry.end ? pathname === entry.to : pathname.startsWith(entry.to);
}

// Authenticated shell (#52 redesign, #829 MUI conversion): a permanent
// `Drawer` — the brand's navigation spine, tinted `--lavender` paper per the
// confirmed direction (DIRECTION.md), not the aubergine `--brand` slab this
// screen painted before — with the destinations grouped by job, each with a
// lucide glyph. Role-tiered (#103): links and whole groups hide per role; the
// API enforces the policy on every gated endpoint regardless.
//
// Below 900px the sidebar gives way to a bottom tab bar + More sheet
// (BottomNav) — the wrapping top bar it used to become ate a third of a
// phone screen. Both navs render from the same nav model (nav.tsx, D5), so
// the role gates live in one place.
export function AppLayout() {
  const { t } = useTranslation("nav");
  const { t: tc } = useTranslation("common");
  const { logout, isAdmin, role } = useAuth();
  const { farm, loadFailed, refresh } = useFarm();
  const navigate = useNavigate();
  const location = useLocation();

  const groups = navGroups(role, isAdmin);
  const tabs = tabEntries(groups);

  // #485 — the banner below is a role="alert" and announces itself, except
  // when a dialog has it inert. A read that fails while the user is mid-dialog
  // is exactly when that bites: the warning is on screen the moment they close
  // it, but nothing would ever have said so.
  const farmWarning = loadFailed
    ? (farm === null ? t("farmLoadFailedNeverLoaded") : t("farmLoadFailedStale"))
    : null;
  const missedFarmWarning = useMissedAnnouncement(farmWarning);

  // Per-page document.title: the active nav entry's translated label + the
  // shared suffix (e.g. "Dashboard — Cluckwork"), so the browser tab/history
  // reads like a real page rather than staying on whatever the last screen set.
  // `nav` is English-only, but the entry is matched the same way BottomNav
  // marks a tab current (`end` -> exact match, else a prefix match).
  useEffect(() => {
    const active = groups.flatMap((g) => g.entries).find((e) => matches(location.pathname, e));
    document.title = active ? `${t(active.labelKey)}${t("titleSuffix")}` : "Cluckwork";
  }, [groups, location.pathname, t]);

  async function onLogout() {
    await logout();
    navigate("/login", { replace: true });
  }

  return (
    <Box sx={{ display: "flex", minHeight: "100dvh" }}>
      {/* First focusable element: lets a keyboard/screen-reader user jump past
          the nav links straight to the screen content (#182, Task 7). */}
      <a href="#main-content" className="skip-link">{t("skipToContent")}</a>

      <Drawer
        variant="permanent"
        sx={{
          display: { xs: "none", md: "block" },
          width: "var(--sidebar-w)",
          flexShrink: 0,
          "& .MuiDrawer-paper": {
            width: "var(--sidebar-w)", boxSizing: "border-box", position: "sticky", top: 0, height: "100dvh",
            display: "flex", flexDirection: "column",
          },
        }}
      >
        <Box sx={{ py: 3 }}>
          <FarmBrand />
        </Box>

        <List component="nav" aria-label={t("primaryNavAriaLabel")} sx={{ flexGrow: 1, overflowY: "auto", py: 0 }}>
          {groups.map((g) => (
            <Fragment key={g.labelKey}>
              <ListSubheader className="nav-group-label" component="p">{t(g.labelKey)}</ListSubheader>
              {g.entries.map((e) => (
                <ListItemButton
                  key={e.to}
                  component={NavLink}
                  to={e.to}
                  selected={matches(location.pathname, e)}
                >
                  <ListItemIcon sx={{ minWidth: 32 }}>
                    <e.Icon size={ICON} strokeWidth={1.5} aria-hidden />
                  </ListItemIcon>
                  <ListItemText primary={t(e.labelKey)} />
                </ListItemButton>
              ))}
            </Fragment>
          ))}
        </List>

        <Box sx={{ mt: "auto", px: 1, py: 1.5, borderTop: "1px solid var(--hairline)" }}>
          <ThemeToggle iconSize={ICON} />
          <Button variant="text" onClick={onLogout} startIcon={<LogOut size={ICON} strokeWidth={1.5} aria-hidden />}
            sx={{ justifyContent: "flex-start", width: "100%" }}>
            {t("signOut")}
          </Button>
          {/* #458 — set at build time (VITE_APP_VERSION, release-please-owned
              via web/.env.production); absent in dev builds, so this line
              simply doesn't render rather than showing "vundefined". */}
          {APP_VERSION && (
            <Typography variant="caption" sx={{ display: "block", px: 1, pt: 0.5, color: "text.secondary" }}>
              {t("versionLabel", { version: APP_VERSION })}
            </Typography>
          )}
        </Box>
      </Drawer>

      <Box component="main" className="content" id="main-content" tabIndex={-1} sx={{ flexGrow: 1, minWidth: 0 }}>
        {/* Carries the warning the banner below could not announce because a
            dialog had it inert (#485), and stays empty otherwise so the two
            never say the same thing twice.

            `aria-live="assertive"` + `aria-atomic` rather than `role="alert"`,
            which is shorthand for exactly that pair. The distinction matters
            because this element is always mounted, and `role="alert"` is how
            ~20 error banners across the app mark themselves — the E2E suite
            reads "no alert on screen" as "nothing has gone wrong", so a
            permanent one would answer every such query and quietly retire the
            check. The CONDITIONAL banner below keeps the role, and with it its
            place in that net. */}
        <p className="sr-only" aria-live="assertive" aria-atomic="true">{missedFarmWarning}</p>

        {/* A farm we never got is not a cosmetic loss: §4.5 formatting and —
            since #123 — every date field's ceiling come from it, so without a
            farm the pickers silently follow the DEVICE's day and the screen
            looks perfectly healthy while being a day out. Say so, and offer the
            read again, rather than degrade in silence (codex review of #123).
            The wording is picked above, where the offscreen region reads it
            too: never got one -> the pickers follow the DEVICE's day; got one
            then a re-read failed -> what is on screen is what a save was meant
            to replace, so a new timezone silently does not apply (round 2:
            codex + pi). */}
        {farmWarning !== null && (
          <Alert severity="warning" className="farm-warning"
            action={<Button color="inherit" size="small" onClick={() => void refresh()}>{t("tryAgain")}</Button>}>
            {farmWarning}
          </Alert>
        )}

        {/* Contain a routed screen's render throw to this pane — the sidebar and
            tab bar stay usable (#140). Keyed by location.key so every navigation
            remounts a fresh boundary: that recovers the screen on nav — even a
            same-path retry when the dashboard ("/") itself crashed, since
            react-router mints a new key each time — and avoids the double-catch
            a resetKey-diffing boundary hits when you navigate into a screen that
            throws on its first render. */}
        <ErrorBoundary key={location.key} scope="screen">
          <Suspense fallback={<p className="muted" role="status">{tc("loading")}</p>}>
            <Outlet />
          </Suspense>
        </ErrorBoundary>
      </Box>

      <BottomNav groups={groups} tabs={tabs} onLogout={onLogout} />
    </Box>
  );
}
