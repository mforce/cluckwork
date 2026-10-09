import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, within } from "@testing-library/react";
import { AccountPage } from "./AccountPage";
import { ConnectedAppsPage } from "./ConnectedAppsPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  disconnectMyApp, disconnectUserApp, listFarmConnectedApps, listMyConnectedApps, listUsers,
} from "../api/cluckwork";
import type { AppConnection } from "../api/cluckwork";

vi.mock("../api/cluckwork", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/cluckwork")>()),
  listMyConnectedApps: vi.fn(),
  disconnectMyApp: vi.fn(),
  listFarmConnectedApps: vi.fn(),
  disconnectUserApp: vi.fn(),
  listUsers: vi.fn(),
}));

const DAY = 86_400_000;
const ago = (days: number) => new Date(Date.now() - days * DAY).toISOString();
const app = (over: Partial<AppConnection>): AppConnection => ({
  userId: "u1", clientId: "c1", appName: "Claude Desktop", scopes: ["farm:read", "daily-entries:write"],
  connectedAtUtc: ago(60), lastUsedAtUtc: ago(0), ...over,
});

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(disconnectMyApp).mockResolvedValue(undefined);
  vi.mocked(disconnectUserApp).mockResolvedValue(undefined);
});

async function confirmDisconnect(appName: string) {
  const dialog = await screen.findByRole("dialog", { name: `Disconnect ${appName}?` });
  fireEvent.click(within(dialog).getByRole("button", { name: "Disconnect" }));
}

describe("Account › Connected apps (#799)", () => {
  it("counts the apps, nudges about the idle one, and names permissions in the consent screen's words", async () => {
    vi.mocked(listMyConnectedApps).mockResolvedValue([
      app({}),
      app({ clientId: "c2", appName: "Farm Notes Helper", scopes: ["farm:read"], lastUsedAtUtc: ago(47) }),
    ]);
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    expect(await screen.findByText("2 connected apps can act as you.")).toBeInTheDocument();
    expect(screen.getByText("Farm Notes Helper has not been used for 47 days. Disconnect it if you no longer use it."))
      .toBeInTheDocument();
    expect(screen.getByText("Read farm data, Record daily entries")).toBeInTheDocument();
    expect(screen.getByText("Not used for 47 days")).toBeInTheDocument();
  });

  it("disconnects after confirmation and says when it stops working", async () => {
    vi.mocked(listMyConnectedApps).mockResolvedValueOnce([app({})]).mockResolvedValueOnce([]);
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    fireEvent.click(await screen.findByRole("button", { name: "Disconnect Claude Desktop" }));
    await confirmDisconnect("Claude Desktop");

    expect(await screen.findByText("Claude Desktop is disconnected. It stops working on its next request."))
      .toBeInTheDocument();
    expect(disconnectMyApp).toHaveBeenCalledWith("c1");
    expect(await screen.findByText("No apps are connected. An app you allow to act as you appears here."))
      .toBeInTheDocument();
  });

  it("does nothing when the confirmation is cancelled", async () => {
    vi.mocked(listMyConnectedApps).mockResolvedValue([app({})]);
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    fireEvent.click(await screen.findByRole("button", { name: "Disconnect Claude Desktop" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(disconnectMyApp).not.toHaveBeenCalled();
  });
});

describe("Setup › Connected apps, the Owner's farm-wide page (#799)", () => {
  beforeEach(() => {
    vi.mocked(listUsers).mockResolvedValue([
      { id: "u1", email: "ana@farm.local", displayName: "Ana Reyes", role: "Worker", disabledAt: null },
      { id: "u2", email: "ben@farm.local", displayName: "Ben Cruz", role: "Manager", disabledAt: null },
    ]);
    vi.mocked(listFarmConnectedApps).mockResolvedValue([
      app({ userId: "u2", clientId: "c9", appName: "ChatGPT" }),
      app({}),
      app({ clientId: "c2", appName: "Farm Notes Helper" }),
    ]);
  });

  it("filters by person and counts what it shows", async () => {
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });

    expect(await screen.findByText("3 connections, 2 people")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Person"), { target: { value: "u2" } });

    expect(screen.getByText("1 connection, 1 person")).toBeInTheDocument();
    expect(screen.queryByText("Claude Desktop")).not.toBeInTheDocument();
  });

  it("disconnects the person's app, not the Owner's", async () => {
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });

    fireEvent.click(await screen.findByRole("button", { name: "Disconnect ChatGPT for Ben Cruz" }));
    await confirmDisconnect("ChatGPT");

    await screen.findByText("ChatGPT is disconnected. It stops working on its next request.");
    expect(disconnectUserApp).toHaveBeenCalledWith("u2", "c9");
  });
});
