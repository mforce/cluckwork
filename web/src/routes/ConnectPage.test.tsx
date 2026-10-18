import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Link, Route, Routes } from "react-router";
import { ConnectPage } from "./ConnectPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { account } from "../test/fixtures";
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
  clientId: "c1", clientName: "Claude Desktop", verifiedDomain: null, redirectHost: "127.0.0.1",
  scopes: ["farm:read", "daily-entries:write"], alreadyAllowed: [], usable: ["farm:read", "daily-entries:write"],
  alreadyApproved: false, assignedFlocks: null,
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
      farm: account({ name: "Hen House" }),
      token: role ? { sub: "u1", role } : { sub: "u1" },
      me: { id: "u1", email: "ana.reyes@meadowlark.farm", name: null, role: role ?? "Worker", language: null, preferredStepperUnit: null },
    },
  );
  await screen.findByRole("heading", { level: 1 });
}

const EVERY_SCOPE = ["farm:read", "daily-entries:write", "daily-entries:submit", "operations:write", "expenses:write"];
const rows = () => within(screen.getByRole("list", { name: "It asks to" })).getAllByRole("listitem");
const checked = () => screen.getAllByRole("checkbox").filter((box) => (box as HTMLInputElement).checked)
  .map((box) => document.querySelector(`label[for="${box.id}"]`)!.textContent!.replace(/ New$/, ""));
const untick = (label: string) => fireEvent.click(screen.getByRole("checkbox", { name: new RegExp(`^${label}`) }));

async function allowWith(password: string) {
  fireEvent.change(screen.getByLabelText(/Your current password/), { target: { value: password } });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Allow" })); });
}

describe("ConnectPage (#798, consent D)", () => {
  it("asks for the request it was opened with and shows the compact card", async () => {
    await show(REQUEST);

    expect(askConsent).toHaveBeenCalledWith(SEARCH, expect.any(AbortSignal));
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
    expect(screen.getByText("Connections you allowed before keep their access until you choose Disconnect in Account › Connected apps.")).toBeInTheDocument();
    expect(screen.getByText("Details").closest("details")).not.toHaveAttribute("open");
  });

  it("ticks every permission the role can use on a first request", async () => {
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE.filter((s) => s !== "expenses:write") }, null);

    expect(checked()).toEqual(["Read farm data", "Record daily entries", "Submit daily entries", "Record farm operations"]);
    expect(screen.getAllByRole("checkbox")).toHaveLength(4);
  });

  it("names the signed-in account prominently, not as a muted caption", async () => {
    await show(REQUEST);

    const email = screen.getByText("Signed in as").parentElement!;
    expect(email).toHaveTextContent("Signed in as ana.reyes@meadowlark.farm");
    expect(getComputedStyle(email).fontWeight).toBe("600");
  });

  // #1212 — `--brand` is a fill in both themes, about 1.3:1 as text at night.
  // styles.test.ts pins --stat-accent's contrast per palette and mode.
  it("colours the farm name and role badge with the theme-aware accent", async () => {
    await show(REQUEST);

    expect(getComputedStyle(screen.getByText("Hen House")).color).toBe("var(--stat-accent)");
    const badge = getComputedStyle(screen.getByText("Your role:").parentElement!);
    expect(badge.color).toBe("var(--stat-accent)");
  });

  it("spends a step-up grant on Allow and sends the browser to the app", async () => {
    vi.mocked(stepUp).mockResolvedValue({ token: "grant-1", expiresAt: "" });
    vi.mocked(approveConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?code=abc" });
    await show(REQUEST);

    await allowWith("pw");

    expect(stepUp).toHaveBeenCalledWith("pw");
    expect(approveConsent).toHaveBeenCalledWith(SEARCH, "grant-1", ["farm:read", "daily-entries:write"]);
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

  // #1326 — the maintainer, 2026-10-18: a fully approved app still shows its permissions.
  it("shows an already-approved app's permissions, all ticked, with the password", async () => {
    await show({ ...REQUEST, alreadyAllowed: REQUEST.scopes, alreadyApproved: true });

    expect(screen.getByRole("heading", { name: "Reconnect Claude Desktop?" })).toBeInTheDocument();
    expect(checked()).toEqual(["Read farm data", "Record daily entries"]);
    expect(screen.getByText("Details")).toBeInTheDocument();
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

  async function detailsFor(request: ConsentRequest, role: string | null) {
    await show(request, role);
    fireEvent.click(screen.getByText("Details"));
    return screen.getByText("It acts as you, so it can never do more than you can.").parentElement!;
  }

  it("limits a flock-scoped Worker's app to the assigned flocks", async () => {
    const details = await detailsFor({ ...REQUEST, assignedFlocks: ["House A", "House B"] }, null);

    expect(details).toHaveTextContent(
      "It can read farm data only for the flocks assigned to you: House A, House B. "
      + "It can record only for the flocks assigned to you: House A, House B.");
  });

  it("tells a Worker with no assignments that the app reaches every flock", async () => {
    const details = await detailsFor(REQUEST, null);

    expect(details).toHaveTextContent(
      "It can read farm data for every flock. It can record for any flock.");
  });

  it("promises no recording when the app asks only to read", async () => {
    const details = await detailsFor({ ...REQUEST, scopes: ["farm:read"], usable: ["farm:read"] }, "Manager");

    expect(details).toHaveTextContent("It can read farm data for every flock.");
    expect(details).not.toHaveTextContent("record");
  });

  it("lets a Manager's app read and record for every flock", async () => {
    const details = await detailsFor(REQUEST, "Manager");

    expect(details).toHaveTextContent(
      "It can read farm data for every flock. It can record for any flock.");
  });

  it("starts focus at the request, not the password", async () => {
    await show(REQUEST);

    // #1201 — the heading takes focus in a passive effect, after show() sees it in the DOM.
    await waitFor(() => expect(screen.getByRole("heading", { level: 1 })).toHaveFocus());
    expect(screen.getByLabelText(/Your current password/)).not.toHaveFocus();
  });

  it("starts a reconnect at the request too", async () => {
    await show({ ...REQUEST, alreadyAllowed: REQUEST.scopes, alreadyApproved: true });

    await waitFor(() => expect(screen.getByRole("heading", { name: "Reconnect Claude Desktop?" })).toHaveFocus());
  });

  it("returns focus to the password after a wrong one", async () => {
    vi.mocked(stepUp).mockRejectedValue(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong"));
    await show(REQUEST);

    await allowWith("nope");

    await waitFor(() => expect(screen.getByLabelText(/Your current password/)).toHaveFocus());
  });

  // The server says what the role can use; the screen keeps no role list of its own.
  it("marks what the server says the role cannot use as not allowed, with no checkbox", async () => {
    await show({ ...REQUEST, usable: ["farm:read"] }, "ReadOnly");

    const write = within(screen.getByRole("list", { name: "It asks to" })).getAllByRole("listitem")[1];
    expect(write).toHaveTextContent("Not allowed for your role");
    expect(within(write).queryByRole("checkbox")).not.toBeInTheDocument();
  });


  // #1311, #1313 — an app connected for reading and recording reconnects asking for every
  // scope; the three it never had are marked New and come first.
  it("marks the three new permissions New when a connected app asks for more", async () => {
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE, alreadyAllowed: ["farm:read", "daily-entries:write"] }, "Manager");

    expect(rows().map((row) => row.textContent)).toEqual([
      "Submit daily entries NewPuts the day's eggs in stock",
      "Record farm operations NewFeed use, water use and inventory purchases",
      "Record expenses New",
      "Read farm dataAlready allowed",
      "Record daily entriesAlready allowed",
    ]);
  });

  it("marks expenses as not allowed for a Worker and never sends it", async () => {
    vi.mocked(stepUp).mockResolvedValue({ token: "grant", expiresAt: "" });
    vi.mocked(approveConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?code=abc" });
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE.filter((s) => s !== "expenses:write") }, null);

    expect(rows().map((row) => row.textContent?.includes("Not allowed for your role"))).toEqual([false, false, false, false, true]);
    expect(within(rows()[4]).queryByRole("checkbox")).not.toBeInTheDocument();
    await allowWith("pw");
    expect(approveConsent).toHaveBeenCalledWith(SEARCH, "grant", ["farm:read", "daily-entries:write", "daily-entries:submit", "operations:write"]);
  });

  // Purchases are account-wide, so a flock-scoped Worker is told the limit does not reach them.
  it("tells a flock-scoped Worker that purchases reach the whole farm's inventory", async () => {
    const details = await detailsFor({ ...REQUEST, scopes: ["operations:write"], usable: ["operations:write"], assignedFlocks: ["House A"] }, null);

    expect(details).toHaveTextContent(
      "It can record only for the flocks assigned to you: House A. "
      + "Inventory purchases are not tied to a flock: they add to the whole farm's inventory.");
  });

  it("says nothing of purchases when the app asks only for daily entries", async () => {
    const details = await detailsFor({ ...REQUEST, assignedFlocks: ["House A"] }, null);

    expect(details).not.toHaveTextContent("Inventory purchases");
  });

  it("says operations take effect at once", async () => {
    const details = await detailsFor({ ...REQUEST, scopes: ["operations:write"], usable: ["operations:write"] }, "Manager");

    expect(details).toHaveTextContent("Record farm operations: what it records counts at once");
  });

  it("says nothing of taking effect at once for daily entries, which stay drafts", async () => {
    const details = await detailsFor({ ...REQUEST, scopes: ["daily-entries:write"], usable: ["daily-entries:write"] }, "Manager");

    expect(details).not.toHaveTextContent("count at once");
  });

  // #1148 — a metadata-document app: its domain is checked, its name is not.
  it("shows a metadata-document app's verified domain instead of the unverified marker", async () => {
    await show({ ...REQUEST, clientId: "https://claude.ai/oauth/claude-code.json", verifiedDomain: "claude.ai" });

    expect(screen.getByText("Verified domain: claude.ai")).toBeInTheDocument();
    expect(screen.queryByText("Unverified app")).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Details"));
    const details = screen.getByText("Details").closest("details")!;
    expect(details).toHaveTextContent(
      "Cluckwork fetched this app's details itself from https://claude.ai/oauth/claude-code.json, so they come from "
      + "whoever controls that address on claude.ai. The name Claude Desktop is still the app's own choice");
    expect(details).toHaveTextContent(
      "an address on this computer. claude.ai cannot vouch for which program on this computer receives it.");
  });

  it("adds no loopback caveat when a verified app returns to a web host", async () => {
    await show({ ...REQUEST, verifiedDomain: "claude.ai", redirectHost: "claude.ai" });

    fireEvent.click(screen.getByText("Details"));
    expect(screen.queryByText(/cannot vouch/)).not.toBeInTheDocument();
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

  // #1146 — both ways into the refusal focus its heading and announce it politely.
  const OFF_BODY = "This farm doesn't allow connected apps. Ask an Owner.";
  const announced = () => screen.getAllByRole("status").map((region) => region.textContent);

  it("says the farm doesn't allow connected apps, asks no password, and Cancel returns to the app", async () => {
    vi.mocked(askConsent).mockRejectedValue(new ApiError(403, "Auth.ConnectedAppsOff", "off"));
    vi.mocked(declineConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?error=access_denied" });
    renderWithProviders(
      <Routes><Route path="/connect" element={<ConnectPage />} /></Routes>,
      { route: `/connect${SEARCH}`, token: { sub: "u1" } },
    );

    const heading = await screen.findByRole("heading", { name: "Connected apps are off" });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(announced()).toContain(OFF_BODY);
    expect(screen.queryByLabelText(/Your current password/)).not.toBeInTheDocument();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Cancel" })); });
    expect(assign).toHaveBeenCalledWith("http://127.0.0.1:5000/cb?error=access_denied");
  });

  it("switches to the refusal when the farm turns apps off before Allow", async () => {
    vi.mocked(stepUp).mockResolvedValue({ token: "grant", expiresAt: "" });
    vi.mocked(approveConsent).mockRejectedValue(new ApiError(403, "Auth.ConnectedAppsOff", "off"));
    await show(REQUEST);

    expect(announced()).not.toContain(OFF_BODY);

    await allowWith("pw");

    const heading = await screen.findByRole("heading", { name: "Connected apps are off" });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(announced()).toContain(OFF_BODY);
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

  // #1326 — partial consent.
  it("ticks what was allowed before and leaves the new permissions unticked and marked New", async () => {
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE, alreadyAllowed: ["farm:read", "daily-entries:write"] });

    expect(checked()).toEqual(["Read farm data", "Record daily entries"]);
    expect(rows().filter((row) => row.textContent?.includes("New"))).toHaveLength(3);
  });

  it("sends only the ticked permissions after one password check", async () => {
    vi.mocked(stepUp).mockResolvedValue({ token: "grant", expiresAt: "" });
    vi.mocked(approveConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?code=abc" });
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE });

    untick("Submit daily entries");
    untick("Record farm operations");
    untick("Record expenses");
    await allowWith("pw");

    expect(stepUp).toHaveBeenCalledTimes(1);
    expect(approveConsent).toHaveBeenCalledWith(SEARCH, "grant", ["farm:read", "daily-entries:write"]);
  });

  it("cancels an empty choice without asking for a password", async () => {
    vi.mocked(declineConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?error=access_denied" });
    await show(REQUEST);

    untick("Read farm data");
    untick("Record daily entries");

    expect(screen.queryByLabelText(/Your current password/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Allow" })).not.toBeInTheDocument();
    expect(screen.getByText("Nothing is ticked. Choose Cancel to go back to the app without connecting it.")).toBeInTheDocument();
    await act(async () => { fireEvent.submit(screen.getByRole("button", { name: "Cancel" }).closest("form")!); });
    expect(declineConsent).toHaveBeenCalledWith(SEARCH);
    expect(stepUp).not.toHaveBeenCalled();
    expect(approveConsent).not.toHaveBeenCalled();
  });

  it("lets Read farm data be unticked, and then promises no reading", async () => {
    await show(REQUEST, "Manager");

    untick("Read farm data");
    fireEvent.click(screen.getByText("Details"));

    const details = screen.getByText("It acts as you, so it can never do more than you can.").parentElement!;
    expect(details).toHaveTextContent("It can record for any flock.");
    expect(details).not.toHaveTextContent("read");
    expect(screen.queryByText(/Reading covers/)).not.toBeInTheDocument();
  });

  it("names only the ticked permissions that count at once", async () => {
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE }, "Manager");

    untick("Record expenses");
    fireEvent.click(screen.getByText("Details"));

    const details = screen.getByText("It acts as you, so it can never do more than you can.").parentElement!;
    expect(details).toHaveTextContent("Record farm operations: what it records counts at once");
    expect(details).not.toHaveTextContent("Record expenses");
  });

  it("locks the choice while the password and the approval are checked", async () => {
    let resolveStepUp!: (value: { token: string; expiresAt: string }) => void;
    vi.mocked(stepUp).mockReturnValue(new Promise((resolve) => { resolveStepUp = resolve; }));
    vi.mocked(approveConsent).mockResolvedValue({ redirectUri: "http://127.0.0.1:5000/cb?code=abc" });
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE });

    untick("Record expenses");
    await allowWith("pw");
    expect(screen.getAllByRole("checkbox").every((box) => (box as HTMLInputElement).disabled)).toBe(true);
    await act(async () => { resolveStepUp({ token: "grant", expiresAt: "" }); });

    expect(approveConsent).toHaveBeenCalledWith(SEARCH, "grant", ["farm:read", "daily-entries:write", "daily-entries:submit", "operations:write"]);
  });

  it("keeps the choice after a wrong password", async () => {
    vi.mocked(stepUp).mockRejectedValueOnce(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong"));
    await show({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE });

    untick("Record expenses");
    await allowWith("nope");

    expect(await screen.findByRole("alert")).toHaveTextContent("Wrong password.");
    expect(checked()).toEqual(["Read farm data", "Record daily entries", "Submit daily entries", "Record farm operations"]);
  });

  it("drops the choice when the authorization request changes", async () => {
    const NEXT = "?client_id=c2&scope=farm%3Aread";
    let resolveNext!: (value: ConsentRequest) => void;
    vi.mocked(askConsent).mockImplementation((search) => search === SEARCH
      ? Promise.resolve({ ...REQUEST, scopes: EVERY_SCOPE, usable: EVERY_SCOPE })
      : new Promise((resolve) => { resolveNext = resolve; }));
    renderWithProviders(
      <Routes><Route path="/connect" element={<><ConnectPage /><Link to={`/connect${NEXT}`}>next</Link></>} /></Routes>,
      { route: `/connect${SEARCH}`, farm: account(), token: { sub: "u1", role: "Manager" } },
    );
    await screen.findByRole("heading", { level: 1 });
    untick("Record expenses");

    fireEvent.click(screen.getByText("next"));

    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.getByText("Loading the request…")).toBeInTheDocument();
    await act(async () => { resolveNext({ ...REQUEST, clientId: "c2", scopes: ["farm:read"], usable: ["farm:read"] }); });
    expect(checked()).toEqual(["Read farm data"]);
  });

  it("aborts the request it was loading when the authorization request changes", async () => {
    vi.mocked(askConsent).mockImplementation(() => new Promise(() => {}));
    renderWithProviders(
      <Routes><Route path="/connect" element={<><ConnectPage /><Link to="/connect?client_id=c2">next</Link></>} /></Routes>,
      { route: `/connect${SEARCH}`, farm: account(), token: { sub: "u1", role: "Manager" } },
    );
    const first = vi.mocked(askConsent).mock.calls[0]![1]!;

    fireEvent.click(screen.getByText("next"));

    await waitFor(() => expect(first.aborted).toBe(true));
    expect(askConsent).toHaveBeenLastCalledWith("?client_id=c2", expect.any(AbortSignal));
  });

  it("asks for no unticking when the role can use none of the requested permissions", async () => {
    await show({ ...REQUEST, scopes: ["expenses:write"], usable: [] }, null);

    expect(screen.queryByText("Untick any permission you don't want to allow.")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.getByText("Nothing is ticked. Choose Cancel to go back to the app without connecting it.")).toBeInTheDocument();
  });

  // A refusal belongs to the read it answered: coming back to the same request reads it
  // afresh, and a fresh answer shows the request again.
  it("forgets a refusal when the person leaves the request and comes back to it", async () => {
    const OTHER = "?client_id=c2&scope=farm%3Aread";
    vi.mocked(askConsent).mockImplementation((search) =>
      Promise.resolve(search === SEARCH ? REQUEST : { ...REQUEST, clientId: "c2", scopes: ["farm:read"], usable: ["farm:read"] }));
    vi.mocked(stepUp).mockResolvedValue({ token: "grant", expiresAt: "" });
    vi.mocked(approveConsent).mockRejectedValue(new ApiError(403, "Auth.ConnectedAppsOff", "off"));
    renderWithProviders(
      <Routes><Route path="/connect" element={<><ConnectPage />
        <Link to={`/connect${OTHER}`}>other</Link><Link to={`/connect${SEARCH}`}>back</Link></>} /></Routes>,
      { route: `/connect${SEARCH}`, farm: account(), token: { sub: "u1", role: "Manager" } },
    );
    await screen.findByRole("heading", { name: "Let Claude Desktop act as you?" });
    await allowWith("pw");
    await screen.findByRole("heading", { name: "Connected apps are off" });

    fireEvent.click(screen.getByText("other"));
    await screen.findByRole("heading", { name: "Let Claude Desktop act as you?" });
    fireEvent.click(screen.getByText("back"));

    await waitFor(() => expect(askConsent).toHaveBeenCalledTimes(3));
    expect(await screen.findByRole("heading", { name: "Let Claude Desktop act as you?" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Connected apps are off" })).not.toBeInTheDocument();
  });
});
