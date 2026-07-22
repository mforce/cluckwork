import { NavLink, Outlet, useNavigate } from "react-router-dom";
import {
  Bird, Boxes, ChartColumn, CircleHelp, ClipboardList, Download, Egg, History,
  LayoutDashboard, LogOut, Package, ScrollText, ShoppingCart, Tags,
  UserCog, Users, Wallet, Droplets,
} from "lucide-react";
import { useAuth } from "../auth/useAuth";
import { ThemeToggle } from "../components/ThemeToggle";

const ICON = 17;

// Authenticated shell (#52 redesign): an aubergine sidebar — the brand's
// navigation spine — with the 15+ destinations grouped by job, each with a
// lucide glyph. Role-tiered (#103): links and whole groups hide per role; the
// API enforces the policy on every gated endpoint regardless. Below 900px the
// sidebar collapses to a wrapping top bar (styles.css). Design-only: same
// routes, same role gates. Icons are decorative (aria-hidden) so link names —
// and the tests that query them — are unchanged.
export function AppLayout() {
  const { logout, isAdmin, role } = useAuth();
  const navigate = useNavigate();
  // Sales users skip production capture; ReadOnly sees views only.
  const canProduce = role !== "Sales" && role !== "ReadOnly";

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
        <nav>
          <div className="nav-group">
            <p className="nav-group-label">Overview</p>
            <NavLink to="/" end><LayoutDashboard size={ICON} aria-hidden /><span>Dashboard</span></NavLink>
          </div>

          {canProduce && (
            <div className="nav-group">
              <p className="nav-group-label">Production</p>
              <NavLink to="/daily-entry"><ClipboardList size={ICON} aria-hidden /><span>Daily entry</span></NavLink>
              <NavLink to="/flocks"><Bird size={ICON} aria-hidden /><span>Flocks</span></NavLink>
              <NavLink to="/water"><Droplets size={ICON} aria-hidden /><span>Water</span></NavLink>
              <NavLink to="/inventory"><Boxes size={ICON} aria-hidden /><span>Inventory</span></NavLink>
            </div>
          )}

          <div className="nav-group">
            <p className="nav-group-label">Sales &amp; stock</p>
            <NavLink to="/stock"><Egg size={ICON} aria-hidden /><span>Stock</span></NavLink>
            {role !== "ReadOnly" && <NavLink to="/customers"><Users size={ICON} aria-hidden /><span>Customers</span></NavLink>}
            {role !== "ReadOnly" && <NavLink to="/sales"><ShoppingCart size={ICON} aria-hidden /><span>Sales</span></NavLink>}
            <NavLink to="/history"><History size={ICON} aria-hidden /><span>History</span></NavLink>
          </div>

          <div className="nav-group">
            <p className="nav-group-label">Insights</p>
            <NavLink to="/reports"><ChartColumn size={ICON} aria-hidden /><span>Reports</span></NavLink>
            {isAdmin && <NavLink to="/expenses"><Wallet size={ICON} aria-hidden /><span>Expenses</span></NavLink>}
          </div>

          {isAdmin && (
            <div className="nav-group">
              <p className="nav-group-label">Setup</p>
              <NavLink to="/grades"><Tags size={ICON} aria-hidden /><span>Grades</span></NavLink>
              <NavLink to="/products"><Package size={ICON} aria-hidden /><span>Products</span></NavLink>
              {role === "Admin" && <NavLink to="/users"><UserCog size={ICON} aria-hidden /><span>Users</span></NavLink>}
              <NavLink to="/audit"><ScrollText size={ICON} aria-hidden /><span>Audit</span></NavLink>
              <NavLink to="/export"><Download size={ICON} aria-hidden /><span>Export</span></NavLink>
            </div>
          )}

          <div className="nav-group">
            <p className="nav-group-label">Help</p>
            <NavLink to="/help"><CircleHelp size={ICON} aria-hidden /><span>Help</span></NavLink>
          </div>
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
    </div>
  );
}
