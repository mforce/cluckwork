import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, fireEvent, within, waitFor } from "@testing-library/react";
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
  userId: "u1", clientId: "c1", appName: "Claude Desktop", scopes: ["daily-entries:write", "farm:read"],
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

    const notice = await screen.findByText("Claude Desktop is disconnected. It stops working on its next request.");
    expect(notice.closest('[role="status"]')).not.toBeNull();
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

// #799 review round 1 — the trigger's row disappears on reload, so focus must land
// on a control that survives: the next row, else the previous one, else the section.
const three = [
  app({ clientId: "c1", appName: "Alpha" }),
  app({ clientId: "c2", appName: "Bravo" }),
  app({ clientId: "c3", appName: "Charlie" }),
];

async function disconnectByKeyboard(name: string, appName: string) {
  const trigger = await screen.findByRole("button", { name });
  trigger.focus();
  fireEvent.click(trigger);
  await confirmDisconnect(appName);
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
}

describe("Connected apps keeps keyboard focus after Disconnect (#799)", () => {
  it("Account: the last app gone, focus moves to the panel", async () => {
    vi.mocked(listMyConnectedApps).mockResolvedValueOnce([app({})]).mockResolvedValue([]);
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    await disconnectByKeyboard("Disconnect Claude Desktop", "Claude Desktop");

    await waitFor(() => expect(screen.getByRole("button", { name: "Connected apps" })).toHaveFocus());
  });

  it("Account: a middle app gone, focus moves to the next app", async () => {
    vi.mocked(listMyConnectedApps).mockResolvedValueOnce(three).mockResolvedValue([three[0], three[2]]);
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    await disconnectByKeyboard("Disconnect Bravo", "Bravo");

    await waitFor(() => expect(screen.getByText("Charlie").closest("summary")).toHaveFocus());
  });

  it("Owner page: the last connection gone, focus moves to the heading", async () => {
    vi.mocked(listUsers).mockResolvedValue([
      { id: "u1", email: "ana@farm.local", displayName: "Ana Reyes", role: "Worker", disabledAt: null },
    ]);
    vi.mocked(listFarmConnectedApps).mockResolvedValueOnce([app({})]).mockResolvedValue([]);
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });

    await disconnectByKeyboard("Disconnect Claude Desktop for Ana Reyes", "Claude Desktop");

    await waitFor(() => expect(screen.getByRole("heading", { name: "Connected apps" })).toHaveFocus());
  });

  it("Owner page: a middle connection gone, focus moves to the next one's Disconnect", async () => {
    vi.mocked(listUsers).mockResolvedValue([
      { id: "u1", email: "ana@farm.local", displayName: "Ana Reyes", role: "Worker", disabledAt: null },
    ]);
    vi.mocked(listFarmConnectedApps).mockResolvedValueOnce(three).mockResolvedValue([three[0], three[2]]);
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });

    await disconnectByKeyboard("Disconnect Bravo for Ana Reyes", "Bravo");

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect Charlie for Ana Reyes" })).toHaveFocus());
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

  // #799 review round 1 — the selected person's last app disconnected: their option
  // goes, and the filter must show everyone rather than an empty list behind "Everyone".
  it("shows everyone again when the selected person has no app left", async () => {
    vi.mocked(listFarmConnectedApps).mockReset()
      .mockResolvedValueOnce([app({}), app({ userId: "u2", clientId: "c9", appName: "ChatGPT" })])
      .mockResolvedValue([app({})]);
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });
    await screen.findByText("2 connections, 2 people");
    fireEvent.change(screen.getByLabelText("Person"), { target: { value: "u2" } });

    fireEvent.click(screen.getByRole("button", { name: "Disconnect ChatGPT for Ben Cruz" }));
    await confirmDisconnect("ChatGPT");

    await waitFor(() => expect(screen.queryByRole("option", { name: "Ben Cruz" })).not.toBeInTheDocument());
    expect(screen.getByLabelText("Person")).toHaveValue("");
    expect(screen.getByText("Claude Desktop")).toBeInTheDocument();
  });

  it("disconnects the person's app, not the Owner's", async () => {
    renderWithProviders(<ConnectedAppsPage />, { token: { sub: "owner", role: "Admin" } });

    fireEvent.click(await screen.findByRole("button", { name: "Disconnect ChatGPT for Ben Cruz" }));
    await confirmDisconnect("ChatGPT");

    await screen.findByText("ChatGPT is disconnected. It stops working on its next request.");
    expect(disconnectUserApp).toHaveBeenCalledWith("u2", "c9");
  });
});
