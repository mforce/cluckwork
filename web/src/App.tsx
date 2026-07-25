import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import { AuthProvider } from "./auth/AuthContext";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { FarmProvider } from "./farm/FarmContext";
import { ProtectedRoute } from "./routes/ProtectedRoute";
import { AppLayout } from "./routes/AppLayout";
import { Login } from "./routes/Login";
import { Dashboard } from "./routes/Dashboard";
import { DailyEntryPage } from "./routes/DailyEntryPage";
import { StockPage } from "./routes/StockPage";
import { CustomersPage } from "./routes/CustomersPage";
import { SalesPage } from "./routes/SalesPage";
import { HistoryPage } from "./routes/HistoryPage";
import { GradesPage } from "./routes/GradesPage";
import { FlocksPage } from "./routes/FlocksPage";
import { InventoryPage } from "./routes/InventoryPage";
import { HelpPage } from "./routes/HelpPage";
import { AccountPage } from "./routes/AccountPage";
import { WaterPage } from "./routes/WaterPage";
import { ExpensesPage } from "./routes/ExpensesPage";
import { ReportsPage } from "./routes/ReportsPage";
import { AuditPage } from "./routes/AuditPage";
import { ExportPage } from "./routes/ExportPage";
import { ProductsPage } from "./routes/ProductsPage";
import { UsersPage } from "./routes/UsersPage";
import { SettingsPage } from "./routes/SettingsPage";

export function App() {
  return (
    // Outer net, outside the providers: catches a throw from the shell
    // (AppLayout, the protected-route gate) OR from AuthProvider/BrowserRouter
    // themselves — AuthProvider reads storage during render, so it can throw.
    // The per-screen boundary is nested inside the shell and cannot see any of
    // these; without this one they are the blank page. Its fallback is a plain
    // anchor, so it needs no router.
    <ErrorBoundary scope="app">
      <AuthProvider>
        <BrowserRouter>
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route element={<ProtectedRoute />}>
              {/* Inside the auth gate (it needs a token to read /account) and
                  outside the shell, so the sidebar's branding slot and every
                  screen's date fields read the same farm (#123). */}
              <Route element={<FarmProvider><AppLayout /></FarmProvider>}>
                <Route index element={<Dashboard />} />
                <Route path="daily-entry" element={<DailyEntryPage />} />
                <Route path="stock" element={<StockPage />} />
                <Route path="customers" element={<CustomersPage />} />
                <Route path="sales" element={<SalesPage />} />
                <Route path="history" element={<HistoryPage />} />
                <Route path="grades" element={<GradesPage />} />
                <Route path="products" element={<ProductsPage />} />
                <Route path="flocks" element={<FlocksPage />} />
                <Route path="inventory" element={<InventoryPage />} />
                <Route path="water" element={<WaterPage />} />
                <Route path="expenses" element={<ExpensesPage />} />
                <Route path="reports" element={<ReportsPage />} />
                <Route path="audit" element={<AuditPage />} />
                <Route path="export" element={<ExportPage />} />
                <Route path="users" element={<UsersPage />} />
                <Route path="settings" element={<SettingsPage />} />
                <Route path="account" element={<AccountPage />} />
                <Route path="help" element={<HelpPage />} />
              </Route>
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </ErrorBoundary>
  );
}
