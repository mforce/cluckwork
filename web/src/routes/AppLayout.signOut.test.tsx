import { describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { renderWithProviders } from "../test/renderWithProviders";
import i18n from "../i18n";
import { AppLayout } from "./AppLayout";

// #1277 review U2, V3 — one sign-out at a time: while its request is out, Sign
// out is disabled in the rail and the More menu, and a second press sends
// nothing.
describe("Sign out while a sign-out is in flight", () => {
  it("is disabled, sends one revoke, and the app reaches the login page when it answers", async () => {
    let answer!: () => void;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL) => (String(input).endsWith("/auth/logout")
      ? new Promise<Response>((resolve) => { answer = () => resolve(new Response(null, { status: 204 })); })
      : new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } })));
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<p>Login page</p>} />
        <Route element={<AppLayout />}><Route index element={<p>Home</p>} /></Route>
      </Routes>,
      { token: { sub: "u1", role: "Worker" } },
    );
    const signOut = () => within(screen.getByRole("complementary")).getByRole("button", { name: i18n.t("nav:signOut") });

    await act(async () => { fireEvent.click(signOut()); });
    await act(async () => { fireEvent.click(signOut()); });

    expect(signOut()).toBeDisabled();
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith("/auth/logout"))).toHaveLength(1);
    await act(async () => { answer(); });
    await waitFor(() => expect(screen.getByText("Login page")).toBeInTheDocument());
  });

  it("keeps the More menu's Sign out disabled after the menu reopens", async () => {
    let answer!: () => void;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL) => (String(input).endsWith("/auth/logout")
      ? new Promise<Response>((resolve) => { answer = () => resolve(new Response(null, { status: 204 })); })
      : new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } })));
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<p>Login page</p>} />
        <Route element={<AppLayout />}><Route index element={<p>Home</p>} /></Route>
      </Routes>,
      { token: { sub: "u1", role: "Worker" } },
    );
    const more = () => screen.getByRole("button", { name: i18n.t("nav:moreButton") });
    const signOut = () => within(screen.getByRole("dialog")).getByRole("button", { name: i18n.t("nav:signOut") });
    await act(async () => { fireEvent.click(more()); });
    expect(signOut()).toBeEnabled();

    try {
      await act(async () => { fireEvent.click(signOut()); });
      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
      await act(async () => { fireEvent.click(more()); });
      expect(signOut()).toBeDisabled();
      await act(async () => { fireEvent.click(signOut()); });

      expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith("/auth/logout"))).toHaveLength(1);
    } finally {
      await act(async () => { answer(); });
    }
    await waitFor(() => expect(screen.getByText("Login page")).toBeInTheDocument());
  });
});
