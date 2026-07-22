import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { AuthProvider } from "./auth/AuthContext";
import { ErrorBoundary } from "./components/ErrorBoundary";
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
import { WaterPage } from "./routes/WaterPage";
import { ExpensesPage } from "./routes/ExpensesPage";
import { ReportsPage } from "./routes/ReportsPage";
import { AuditPage } from "./routes/AuditPage";
import { ExportPage } from "./routes/ExportPage";
import { ProductsPage } from "./routes/ProductsPage";
import { UsersPage } from "./routes/UsersPage";

export function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        {/* Outer net: catches a throw from the shell itself (AppLayout, the
            protected-route gate) that the per-screen boundary — nested inside
            the shell — cannot see. Rare, but its absence is the blank page. */}
        <ErrorBoundary scope="app">
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route element={<ProtectedRoute />}>
              <Route element={<AppLayout />}>
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
                <Route path="help" element={<HelpPage />} />
              </Route>
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </ErrorBoundary>
      </BrowserRouter>
    </AuthProvider>
  );
}
