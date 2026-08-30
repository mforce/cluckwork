import { lazy } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import { AuthProvider } from "./auth/AuthContext";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { UpdatePrompt } from "./pwa/UpdatePrompt";
import { SessionProvider } from "./session/SessionContext";
import { ProtectedRoute } from "./routes/ProtectedRoute";
import { AppLayout } from "./routes/AppLayout";
import { Login } from "./routes/Login";

const Dashboard = lazy(() => import("./routes/Dashboard").then(({ Dashboard }) => ({ default: Dashboard })));
const DailyEntryPage = lazy(() => import("./routes/DailyEntryPage").then(({ DailyEntryPage }) => ({ default: DailyEntryPage })));
const StockPage = lazy(() => import("./routes/StockPage").then(({ StockPage }) => ({ default: StockPage })));
const CustomersPage = lazy(() => import("./routes/CustomersPage").then(({ CustomersPage }) => ({ default: CustomersPage })));
const SalesPage = lazy(() => import("./routes/SalesPage").then(({ SalesPage }) => ({ default: SalesPage })));
const HistoryPage = lazy(() => import("./routes/HistoryPage").then(({ HistoryPage }) => ({ default: HistoryPage })));
const GradesPage = lazy(() => import("./routes/GradesPage").then(({ GradesPage }) => ({ default: GradesPage })));
const ProductsPage = lazy(() => import("./routes/ProductsPage").then(({ ProductsPage }) => ({ default: ProductsPage })));
const FlocksPage = lazy(() => import("./routes/FlocksPage").then(({ FlocksPage }) => ({ default: FlocksPage })));
const InventoryPage = lazy(() => import("./routes/InventoryPage").then(({ InventoryPage }) => ({ default: InventoryPage })));
const FeedPage = lazy(() => import("./routes/FeedPage").then(({ FeedPage }) => ({ default: FeedPage })));
const WaterPage = lazy(() => import("./routes/WaterPage").then(({ WaterPage }) => ({ default: WaterPage })));
const ExpensesPage = lazy(() => import("./routes/ExpensesPage").then(({ ExpensesPage }) => ({ default: ExpensesPage })));
const ReportsPage = lazy(() => import("./routes/ReportsPage").then(({ ReportsPage }) => ({ default: ReportsPage })));
const AuditPage = lazy(() => import("./routes/AuditPage").then(({ AuditPage }) => ({ default: AuditPage })));
const ExportPage = lazy(() => import("./routes/ExportPage").then(({ ExportPage }) => ({ default: ExportPage })));
const UsersPage = lazy(() => import("./routes/UsersPage").then(({ UsersPage }) => ({ default: UsersPage })));
const SettingsPage = lazy(() => import("./routes/SettingsPage").then(({ SettingsPage }) => ({ default: SettingsPage })));
const AccountPage = lazy(() => import("./routes/AccountPage").then(({ AccountPage }) => ({ default: AccountPage })));
const HelpPage = lazy(() => import("./routes/HelpPage").then(({ HelpPage }) => ({ default: HelpPage })));

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
                  screen's date fields read the same farm (#123). SessionProvider
                  is the coordinated authenticated bootstrap (#182): it reads
                  /me + /account together, resolves + switches the UI language,
                  and renders FarmProvider internally so /account is read once. */}
              <Route element={<SessionProvider><AppLayout /></SessionProvider>}>
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
                <Route path="feed" element={<FeedPage />} />
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
      {/* Outside the router and the auth gate: an update is worth offering on
          any screen, including the login page, and it needs no route context.
          Inside the boundary so a throw here can't blank the app (#142). */}
      <UpdatePrompt />
    </ErrorBoundary>
  );
}
