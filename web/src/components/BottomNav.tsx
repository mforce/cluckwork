import { useState } from "react";
import { NavLink } from "react-router-dom";
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
          className="tab"
          aria-haspopup="dialog"
          aria-expanded={moreOpen}
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
