import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { AuthProvider } from "./auth/AuthContext";
import { ProtectedRoute } from "./routes/ProtectedRoute";
import { AppLayout } from "./routes/AppLayout";
import { Login } from "./routes/Login";
import { Dashboard } from "./routes/Dashboard";
import { DailyEntryPage } from "./routes/DailyEntryPage";
import { StockPage } from "./routes/StockPage";
import { Placeholder } from "./routes/Placeholder";

export function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route element={<ProtectedRoute />}>
            <Route element={<AppLayout />}>
              <Route index element={<Dashboard />} />
              <Route path="daily-entry" element={<DailyEntryPage />} />
              <Route path="stock" element={<StockPage />} />
              <Route
                path="sales"
                element={<Placeholder title="Sales" issue="#23 (F3)" />}
              />
            </Route>
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  );
}
