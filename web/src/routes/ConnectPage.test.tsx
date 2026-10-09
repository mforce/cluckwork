import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { ConnectPage } from "./ConnectPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { ApiError, logout, stepUp } from "../api/client";
import { approveConsent, askConsent, declineConsent } from "../api/oauth";
import type { ConsentRequest } from "../api/oauth";

vi.mock("../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/client")>()),
  stepUp: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));
vi.mock("../api/oauth", () => ({
  askConsent: vi.fn(),
  approveConsent: vi.fn(),
  declineConsent: vi.fn(),
  previewConsent: vi.fn(),
}));

const SEARCH = "?client_id=c1&scope=farm%3Aread%20daily-entries%3Awrite";
const REQUEST: ConsentRequest = {
  clientId: "c1", clientName: "Claude Desktop", redirectHost: "127.0.0.1",
  scopes: ["farm:read", "daily-entries:write"], alreadyAllowed: [], alreadyApproved: false, assignedFlocks: null,
};
const assign = vi.fn();
const originalLocation = window.location;

beforeEach(() => {
  Object.defineProperty(window, "location", { configurable: true, value: { ...originalLocation, assign } });
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  Object.defineProperty(window, "location", { configurable: true, value: originalLocation });
});

async function show(request: ConsentRequest, role: string | null = "Manager") {
  vi.mocked(askConsent).mockResolvedValue(request);
  renderWithProviders(
    <Routes><Route path="/connect" element={<ConnectPage />} /></Routes>,
    {
      route: `/connect${SEARCH}`,
      token: role ? { sub: "u1", role } : { sub: "u1" },
      me: { id: "u1", email: "ana.reyes@meadowlark.farm", name: null, role: role ?? "Worker", language: null, preferredStepperUnit: null },
    },
  );
  await screen.findByRole("heading", { level: 1 });
}

async function allowWith(password: string) {
  fireEvent.change(screen.getByLabelText(/Your current password/), { target: { value: password } });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Allow" })); });
}

describe("ConnectPage (#798, consent D)", () => {
  it("asks for the request it was opened with and shows the compact card", async () => {
    await show(REQUEST);

    expect(askConsent).toHaveBeenCalledWith(SEARCH);
    expect(screen.getByRole("heading", { name: "Let Claude Desktop act as you?" })).toBeInTheDocument();
    expect(screen.getByText("Unverified app")).toBeInTheDocument();
    expect(screen.getByText("Returns to this computer")).toBeInTheDocument();
    const rows = within(screen.getByRole("list", { name: "It asks to" })).getAllByRole("listitem");
    expect(rows.map((row) => row.textContent)).toEqual([
      "Read farm dataFlocks, stock, entries and sales",
      "Record daily entries",
    ]);
    expect(screen.getByText("Acts as you, with only your access")).toBeInTheDocument();
    expect(screen.getByText("Your role:").parentElement).toHaveTextContent("Your role: Manager");
    expect(screen.getByText("Disconnect it anytime in Account › Connected apps.")).toBeInTheDocument();
    expect(screen.getByText("Details").closest("details")).not.toHaveAttribute("open");
  });

  it("names the signed-in account prominently, not as a muted caption", async () => {
    await show(REQUEST);

    const email = screen.getByText("Signed in as").parentElement!;
    expect(email).toHaveTextContent("Signed in as ana.reyes@meadowlark.farm");
    expect(getComputedStyle(email).fontWeight).toBe("600");
  });

  it("spends a step-up grant on Allow and sends the browser to the app", async () => {
    vi.mocked(stepUp).mockResolvedValue({ token: "grant-1", expiresAt: "" });
    vi.mocked(approveConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?code=abc" });
    await show(REQUEST);

    await allowWith("pw");

    expect(stepUp).toHaveBeenCalledWith("pw");
    expect(approveConsent).toHaveBeenCalledWith(SEARCH, "grant-1");
    expect(assign).toHaveBeenCalledWith("http://127.0.0.1:5000/cb?code=abc");
  });

  it("says a wrong password connected nothing and asks nothing of the server", async () => {
    vi.mocked(stepUp).mockRejectedValue(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong"));
    await show(REQUEST);

    await allowWith("nope");

    expect(await screen.findByRole("alert")).toHaveTextContent("Wrong password. Nothing was connected.");
    expect(approveConsent).not.toHaveBeenCalled();
    expect(screen.getByLabelText(/Your current password/)).toHaveValue("");
  });

  it("puts the new permission first when the app asks for more", async () => {
    await show({ ...REQUEST, alreadyAllowed: ["farm:read"] });

    expect(screen.getByRole("heading", { name: "Let Claude Desktop do more?" })).toBeInTheDocument();
    const rows = within(screen.getByRole("list", { name: "It asks to" })).getAllByRole("listitem");
    expect(rows.map((row) => row.textContent)).toEqual([
      "Record daily entries New",
      "Read farm dataAlready allowed",
    ]);
  });

  it("asks an already-approved app for the password only", async () => {
    await show({ ...REQUEST, alreadyAllowed: REQUEST.scopes, alreadyApproved: true });

    expect(screen.getByRole("heading", { name: "Reconnect Claude Desktop?" })).toBeInTheDocument();
    expect(screen.getByText("You allowed it before. Enter your password to confirm it's you.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
    expect(screen.queryByText("Details")).not.toBeInTheDocument();
    expect(screen.getByText("Signed in as").parentElement).toHaveTextContent("ana.reyes@meadowlark.farm");
    expect(screen.getByLabelText(/Your current password/)).toBeInTheDocument();
  });

  it("keeps the longer explanations and the sign-out under Details", async () => {
    await show(REQUEST, null);

    fireEvent.click(screen.getByText("Details"));

    const details = screen.getByText("Details").closest("details")!;
    expect(details).toHaveTextContent("It acts as you, so it can never do more than you can.");
    expect(details).toHaveTextContent("Claude Desktop chose its own name.");
    expect(details).toHaveTextContent("Afterwards your browser goes back to 127.0.0.1, an address on this computer.");
    await act(async () => { fireEvent.click(within(details).getByRole("button", { name: "Not you? Sign out" })); });
    expect(logout).toHaveBeenCalled();
  });

  // Details states what this request would get: its scopes, within the user's role and
  // flock scope, never what the role name alone suggests.
  async function detailsFor(request: ConsentRequest, role: string | null) {
    await show(request, role);
    fireEvent.click(screen.getByText("Details"));
    return screen.getByText("It acts as you, so it can never do more than you can.").parentElement!;
  }

  it("limits a flock-scoped Worker's app to the assigned flocks", async () => {
    const details = await detailsFor({ ...REQUEST, assignedFlocks: ["House A", "House B"] }, null);

    expect(details).toHaveTextContent(
      "It can read farm data only for the flocks assigned to you: House A, House B. "
      + "It can record daily entries only for the flocks assigned to you: House A, House B.");
  });

  it("tells a Worker with no assignments that the app reaches every flock", async () => {
    const details = await detailsFor(REQUEST, null);

    expect(details).toHaveTextContent(
      "It can read farm data for every flock. It can record daily entries for any flock.");
  });

  it("promises no recording when the app asks only to read", async () => {
    const details = await detailsFor({ ...REQUEST, scopes: ["farm:read"] }, "Manager");

    expect(details).toHaveTextContent("It can read farm data for every flock.");
    expect(details).not.toHaveTextContent("record");
  });

  it("lets a Manager's app read and record for every flock", async () => {
    const details = await detailsFor(REQUEST, "Manager");

    expect(details).toHaveTextContent(
      "It can read farm data for every flock. It can record daily entries for any flock.");
  });

  it("starts focus at the request, not the password", async () => {
    await show(REQUEST);

    expect(screen.getByRole("heading", { level: 1 })).toHaveFocus();
    expect(screen.getByLabelText(/Your current password/)).not.toHaveFocus();
  });

  it("starts a reconnect at the request too", async () => {
    await show({ ...REQUEST, alreadyAllowed: REQUEST.scopes, alreadyApproved: true });

    expect(screen.getByRole("heading", { name: "Reconnect Claude Desktop?" })).toHaveFocus();
  });

  it("returns focus to the password after a wrong one", async () => {
    vi.mocked(stepUp).mockRejectedValue(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong"));
    await show(REQUEST);

    await allowWith("nope");

    await waitFor(() => expect(screen.getByLabelText(/Your current password/)).toHaveFocus());
  });

  it("marks recording entries as not allowed for a role that cannot record them", async () => {
    await show(REQUEST, "ReadOnly");

    const write = within(screen.getByRole("list", { name: "It asks to" })).getAllByRole("listitem")[1];
    expect(write).toHaveTextContent("Not allowed for your role");
  });

  it("names a web app's host instead of this computer", async () => {
    await show({ ...REQUEST, redirectHost: "app.example" });

    expect(screen.getByText("Returns to app.example")).toBeInTheDocument();
  });

  it("sends Cancel to the server, which answers with the app's redirect", async () => {
    vi.mocked(declineConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?error=access_denied" });
    await show(REQUEST);

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Cancel" })); });

    expect(declineConsent).toHaveBeenCalledWith(SEARCH);
    expect(assign).toHaveBeenCalledWith("http://127.0.0.1:5000/cb?error=access_denied");
  });

  it("shows nothing to approve when the server refuses the request", async () => {
    vi.mocked(askConsent).mockRejectedValue(new ApiError(400, "invalid_request", "bad"));
    renderWithProviders(
      <Routes><Route path="/connect" element={<ConnectPage />} /></Routes>,
      { route: `/connect${SEARCH}`, token: { sub: "u1" } },
    );

    expect(await screen.findByRole("heading", { name: "Nothing to approve" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByLabelText(/Your current password/)).not.toBeInTheDocument());
  });
});
