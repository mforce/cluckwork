import { useEffect, useState } from "react";
import { NavLink, useLocation } from "react-router";
import { LogOut, Menu } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Box, BottomNavigation, BottomNavigationAction } from "@mui/material";
import { Dialog } from "./Dialog";
import { ThemeToggle } from "./ThemeToggle";
import { MD_UP_QUERY } from "../lib/breakpoints";
import type { NavEntry, NavGroup } from "../routes/nav";

const ICON = 24;
// The sentinel `BottomNavigation`'s `value` compares against when the current
// route is not one of the four tabs, so the fifth (More) slot clones as
// selected instead of nothing showing active at all.
const MORE_VALUE = "__more__";

// F: mobile navigation. The sidebar's links wrapped into six rows and ate a
// third of a phone screen (298px of 844). This is the thumb-zone answer: the
// four most-used destinations for the role as fixed tabs, everything else one
// tap away in a bottom sheet. Shown only below 900px; the sidebar owns wider
// screens. Built from the same nav model, so the role gates are not
// duplicated.
//
// D2 pair 13 (#829): `BottomNavigation` + `BottomNavigationAction
// component={NavLink}`, inheriting the variant B ("ruled") overrides #864
// already put in the theme. The fifth slot stays a plain button — it opens a
// sheet, not a route — carrying `aria-haspopup="dialog"`, `aria-expanded` and
// `aria-current="page"` when the route is under More, none of which
// `BottomNavigationAction` models on its own. More itself stays on the
// existing `Dialog` component: #827 (MUI `Dialog` conversion) has not landed,
// so there is no `SwipeableDrawer` question to answer yet — the alternative
// §7 names is exactly what this keeps.
export function BottomNav({
  groups, tabs, onLogout,
}: {
  groups: NavGroup[];
  tabs: NavEntry[];
  onLogout: () => void;
}) {
  const { t } = useTranslation("nav");
  const [moreOpen, setMoreOpen] = useState(false);
  const { pathname } = useLocation();

  // Same route-match rule the sidebar uses (AppLayout.tsx's `matches`): exact
  // for an `end` entry, a prefix match otherwise.
  const matchesTab = (e: NavEntry) => (e.end ? pathname === e.to : pathname.startsWith(e.to));
  const onATab = tabs.some(matchesTab);
  // The value `BottomNavigation` clones `selected` from: the active tab's own
  // route when the screen is one of the four, the More sentinel otherwise —
  // so exactly one action always reads as current.
  const activeValue = onATab ? tabs.find(matchesTab)!.to : MORE_VALUE;

  // A sheet opened on a phone must not survive a resize past the breakpoint —
  // it would hang as a modal over the restored sidebar, and closing would try
  // to return focus to a now-hidden trigger. matchMedia is guarded for jsdom,
  // which does not implement it (see lib/theme.ts).
  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    // MD_UP_QUERY (900px) is the exact boundary the sidebar/tab-bar switch
    // uses (AppLayout.tsx/BottomNav.tsx `sx={{ display: { xs, md } }}`) — a
    // mismatched listener boundary left a sheet opened at exactly 900px open
    // over the now-visible sidebar, its trigger hidden underneath it (#883
    // round 2, finding 2).
    const desktop = window.matchMedia(MD_UP_QUERY);
    const closeOnDesktop = () => { if (desktop.matches) setMoreOpen(false); };
    desktop.addEventListener("change", closeOnDesktop);
    return () => desktop.removeEventListener("change", closeOnDesktop);
  }, []);

  return (
    <>
      <Box
        component="nav"
        aria-label={t("tabBarAriaLabel")}
        sx={{
          display: { xs: "block", md: "none" },
          position: "fixed", left: 0, right: 0, bottom: 0,
          zIndex: 20, // over the page + the entry footer (10), under the dialog (50)
        }}
      >
        {/* --tabbar-h (styles.css) is the bar's whole box — 3.6rem of content
            plus the safe-area inset — and every OTHER sticky footer reads it
            as that combined offset unchanged. The bar itself must not take
            that whole value as its content height, or the actions centre
            inside the inset and the labels sit under a notched phone's home
            indicator: content gets exactly 3.6rem, and the inset becomes its
            own bottom padding underneath (#883 round 2, finding 3). */}
        <BottomNavigation value={activeValue} showLabels
          sx={{ height: "3.6rem", paddingBottom: "env(safe-area-inset-bottom)" }}>
          {tabs.map((e) => (
            <BottomNavigationAction
              key={e.to}
              value={e.to}
              label={t(e.labelKey)}
              component={NavLink}
              to={e.to}
              icon={<e.Icon size={ICON} strokeWidth={1.5} aria-hidden />}
            />
          ))}
          <BottomNavigationAction
            value={MORE_VALUE}
            label={t("moreButton")}
            icon={<Menu size={ICON} strokeWidth={1.5} aria-hidden />}
            className={onATab ? undefined : "active"}
            aria-haspopup="dialog"
            aria-expanded={moreOpen}
            aria-current={onATab ? undefined : "page"}
            onClick={() => setMoreOpen(true)}
          />
        </BottomNavigation>
      </Box>

      {/* The full map, so nothing is unreachable — the tabs are shortcuts into
          it, not a smaller menu. Dialog gives the focus trap, scroll lock and
          bottom-sheet styling for free (#131). */}
      <Dialog open={moreOpen} title={t("menuTitle")} onClose={() => setMoreOpen(false)}>
        <nav className="more-nav" aria-label={t("allSectionsAriaLabel")}>
          {groups.map((g) => (
            <div className="more-group" key={g.labelKey}>
              <p className="more-group-label">{t(g.labelKey)}</p>
              {g.entries.map((e) => (
                <NavLink key={e.to} to={e.to} end={e.end}
                  onClick={() => setMoreOpen(false)}>
                  <e.Icon size={ICON} strokeWidth={1.5} aria-hidden />
                  <span>{t(e.labelKey)}</span>
                </NavLink>
              ))}
            </div>
          ))}
        </nav>
        <div className="more-foot">
          <ThemeToggle iconSize={ICON} />
          <button className="link" onClick={() => { setMoreOpen(false); onLogout(); }}>
            <LogOut size={ICON} strokeWidth={1.5} aria-hidden /><span>{t("signOut")}</span>
          </button>
        </div>
      </Dialog>
    </>
  );
}
