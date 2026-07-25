import { useEffect, useState } from "react";
import { NavLink, useLocation } from "react-router";
import { LogOut, Menu } from "lucide-react";
import { Dialog } from "./Dialog";
import { ThemeToggle } from "./ThemeToggle";
import type { NavEntry, NavGroup } from "../routes/nav";

const ICON = 20;

// F: mobile navigation. The sidebar's 17 links wrapped into six rows and ate a
// third of a phone screen (298px of 844). This is the thumb-zone answer: the
// four most-used destinations for the role as fixed tabs, everything else one
// tap away in a bottom sheet. Shown only below 900px (styles.css); the sidebar
// owns wider screens. Built from the same nav model, so the role gates are not
// duplicated.
export function BottomNav({
  groups, tabs, onLogout,
}: {
  groups: NavGroup[];
  tabs: NavEntry[];
  onLogout: () => void;
}) {
  const [moreOpen, setMoreOpen] = useState(false);
  const { pathname } = useLocation();

  // When the current screen is not one of the four tabs, it lives under More —
  // so More carries the current-page marker, otherwise the bar shows nothing
  // active at all on those routes. NavLink's own `end` matching decides the
  // tabs; this mirrors it for the overflow.
  const onATab = tabs.some((t) => (t.end ? pathname === t.to : pathname.startsWith(t.to)));

  // A sheet opened on a phone must not survive a resize past the breakpoint —
  // it would hang as a modal over the restored sidebar, and closing would try
  // to return focus to a now-hidden trigger. matchMedia is guarded for jsdom,
  // which does not implement it (see lib/theme.ts).
  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const desktop = window.matchMedia("(min-width: 901px)");
    const closeOnDesktop = () => { if (desktop.matches) setMoreOpen(false); };
    desktop.addEventListener("change", closeOnDesktop);
    return () => desktop.removeEventListener("change", closeOnDesktop);
  }, []);

  return (
    <>
      <nav className="tabbar" aria-label="Sections">
        {tabs.map((e) => (
          <NavLink key={e.to} to={e.to} end={e.end} className="tab">
            <e.Icon size={ICON} aria-hidden />
            <span>{e.label}</span>
          </NavLink>
        ))}
        <button
          type="button"
          className={onATab ? "tab" : "tab active"}
          aria-haspopup="dialog"
          aria-expanded={moreOpen}
          aria-current={onATab ? undefined : "page"}
          onClick={() => setMoreOpen(true)}
        >
          <Menu size={ICON} aria-hidden />
          <span>More</span>
        </button>
      </nav>

      {/* The full map, so nothing is unreachable — the tabs are shortcuts into
          it, not a smaller menu. Dialog gives the focus trap, scroll lock and
          bottom-sheet styling for free (#131). */}
      <Dialog open={moreOpen} title="Menu" onClose={() => setMoreOpen(false)}>
        <nav className="more-nav" aria-label="All sections">
          {groups.map((g) => (
            <div className="more-group" key={g.label}>
              <p className="more-group-label">{g.label}</p>
              {g.entries.map((e) => (
                <NavLink key={e.to} to={e.to} end={e.end}
                  onClick={() => setMoreOpen(false)}>
                  <e.Icon size={ICON} aria-hidden />
                  <span>{e.label}</span>
                </NavLink>
              ))}
            </div>
          ))}
        </nav>
        <div className="more-foot">
          <ThemeToggle iconSize={ICON} />
          <button className="link" onClick={() => { setMoreOpen(false); onLogout(); }}>
            <LogOut size={ICON} aria-hidden /><span>Sign out</span>
          </button>
        </div>
      </Dialog>
    </>
  );
}
