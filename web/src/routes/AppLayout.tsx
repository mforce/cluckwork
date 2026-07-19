import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useAuth } from "../auth/useAuth";

// Authenticated shell. Nav targets are placeholders for the Phase 1.0 slices
// (daily entry #F1, stock #F2, customers/sales #F3, history #F4) — screens land
// as their API slices ship.
export function AppLayout() {
  const { logout, isAdmin } = useAuth();
  const navigate = useNavigate();

  async function onLogout() {
    await logout();
    navigate("/login", { replace: true });
  }

  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">Cluckwork</span>
        <nav>
          <NavLink to="/" end>
            Dashboard
          </NavLink>
          <NavLink to="/daily-entry">Daily entry</NavLink>
          <NavLink to="/flocks">Flocks</NavLink>
          <NavLink to="/stock">Stock</NavLink>
          <NavLink to="/inventory">Inventory</NavLink>
          <NavLink to="/water">Water</NavLink>
          <NavLink to="/customers">Customers</NavLink>
          <NavLink to="/sales">Sales</NavLink>
          <NavLink to="/history">History</NavLink>
          <NavLink to="/reports">Reports</NavLink>
          {/* Admin-only screens (#73): the links hide for workers, and the API
              enforces the role on every gated endpoint regardless. */}
          {isAdmin && <NavLink to="/expenses">Expenses</NavLink>}
          {isAdmin && <NavLink to="/grades">Grades</NavLink>}
          {isAdmin && <NavLink to="/users">Users</NavLink>}
          <NavLink to="/help">Help</NavLink>
        </nav>
        <button className="link" onClick={onLogout}>
          Sign out
        </button>
      </header>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
