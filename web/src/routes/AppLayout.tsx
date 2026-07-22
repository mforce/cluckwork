import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { Egg, LogOut } from "lucide-react";
import { useAuth } from "../auth/useAuth";
import { ThemeToggle } from "../components/ThemeToggle";
import { BottomNav } from "../components/BottomNav";
import { navGroups, tabEntries } from "./nav";

const ICON = 17;

// Authenticated shell (#52 redesign): an aubergine sidebar — the brand's
// navigation spine — with the 15+ destinations grouped by job, each with a
// lucide glyph. Role-tiered (#103): links and whole groups hide per role; the
// API enforces the policy on every gated endpoint regardless.
//
// Below 900px the sidebar gives way to a bottom tab bar + More sheet (BottomNav)
// — the wrapping top bar it used to become ate a third of a phone screen. Both
// navs render from the same nav model, so the role gates live in one place.
export function AppLayout() {
  const { logout, isAdmin, role } = useAuth();
  const navigate = useNavigate();

  const groups = navGroups(role, isAdmin);
  const tabs = tabEntries(groups);

  async function onLogout() {
    await logout();
    navigate("/login", { replace: true });
  }

  return (
    <div className="shell">
      <aside className="sidebar">
        <span className="brand">
          <Egg size={20} aria-hidden className="brand-mark" /> Cluckwork
        </span>
        <nav aria-label="Primary">
          {groups.map((g) => (
            <div className="nav-group" key={g.label}>
              <p className="nav-group-label">{g.label}</p>
              {g.entries.map((e) => (
                <NavLink key={e.to} to={e.to} end={e.end}>
                  <e.Icon size={ICON} aria-hidden /><span>{e.label}</span>
                </NavLink>
              ))}
            </div>
          ))}
        </nav>

        <div className="sidebar-foot">
          <ThemeToggle iconSize={ICON} />
          <button className="link" onClick={onLogout}>
            <LogOut size={ICON} aria-hidden /><span>Sign out</span>
          </button>
        </div>
      </aside>

      <main className="content">
        <Outlet />
      </main>

      <BottomNav groups={groups} tabs={tabs} onLogout={onLogout} />
    </div>
  );
}
