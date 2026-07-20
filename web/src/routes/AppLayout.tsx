import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useAuth } from "../auth/useAuth";

// Authenticated shell. Nav targets are placeholders for the Phase 1.0 slices
// (daily entry #F1, stock #F2, customers/sales #F3, history #F4) — screens land
// as their API slices ship.
export function AppLayout() {
  const { logout, isAdmin, role } = useAuth();
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
          {role !== "Sales" && role !== "ReadOnly" && <NavLink to="/daily-entry">Daily entry</NavLink>}
          {/* Role-tiered nav (#103): links hide per role, and the API
              enforces the policy on every gated endpoint regardless.
              Sales users skip production capture; ReadOnly sees views only. */}
          {role !== "Sales" && role !== "ReadOnly" && <NavLink to="/flocks">Flocks</NavLink>}
          <NavLink to="/stock">Stock</NavLink>
          {role !== "Sales" && role !== "ReadOnly" && <NavLink to="/inventory">Inventory</NavLink>}
          {role !== "Sales" && role !== "ReadOnly" && <NavLink to="/water">Water</NavLink>}
          {role !== "ReadOnly" && <NavLink to="/customers">Customers</NavLink>}
          {role !== "ReadOnly" && <NavLink to="/sales">Sales</NavLink>}
          <NavLink to="/history">History</NavLink>
          <NavLink to="/reports">Reports</NavLink>
          {isAdmin && <NavLink to="/expenses">Expenses</NavLink>}
          {isAdmin && <NavLink to="/grades">Grades</NavLink>}
          {isAdmin && <NavLink to="/products">Products</NavLink>}
          {role === "Admin" && <NavLink to="/users">Users</NavLink>}
          {isAdmin && <NavLink to="/audit">Audit</NavLink>}
          {isAdmin && <NavLink to="/export">Export</NavLink>}
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
