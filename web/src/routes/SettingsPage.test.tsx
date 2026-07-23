import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { SettingsPage } from "./SettingsPage";
import { FarmContext } from "../farm/FarmContext";
import type { FarmState } from "../farm/FarmContext";
import {
  LOGO_MAX_BYTES, getFarmLogo, getFarmSettings, removeFarmLogo, updateFarmSettings,
  uploadFarmLogo,
} from "../api/cluckwork";
import type { Account, FarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { account } from "../test/fixtures";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return {
    ...actual,
    getFarmSettings: vi.fn(),
    updateFarmSettings: vi.fn(),
    uploadFarmLogo: vi.fn(),
    removeFarmLogo: vi.fn(),
    getFarmLogo: vi.fn(),
  };
});

const mockGetSettings = vi.mocked(getFarmSettings);
const mockUpdate = vi.mocked(updateFarmSettings);
const mockUpload = vi.mocked(uploadFarmLogo);
const mockRemove = vi.mocked(removeFarmLogo);
const mockGetLogo = vi.mocked(getFarmLogo);

const SETTINGS = (over: Partial<Account> = {}, canChangeCurrency = true): FarmSettings => ({
  settings: account({
    name: "Hen House",
    timeZoneId: "America/Los_Angeles",
    locale: "en-US",
    currencyCode: "USD",
    currencyMinorUnit: 2,
    unitSystem: "Metric",
    version: 7,
    ...over,
  }),
  canChangeCurrency,
});

let refreshed = 0;

async function renderReady(payload: FarmSettings = SETTINGS()) {
  mockGetSettings.mockResolvedValue(payload);
  const value: FarmState = {
    farm: payload.settings,
    refresh: async () => { refreshed += 1; },
  };
  const result = render(
    <FarmContext.Provider value={value}><SettingsPage /></FarmContext.Provider>);
  expect(await screen.findByLabelText("Farm name")).toBeInTheDocument();
  return result;
}

const dialog = () => screen.getByRole("dialog");

// A file whose SIZE is what the test is about — declared rather than allocated,
// so "over the cap" costs no megabyte.
function imageOfSize(bytes: number, name = "logo.png", type = "image/png"): File {
  const file = new File(["x"], name, { type });
  Object.defineProperty(file, "size", { value: bytes });
  return file;
}

beforeEach(() => {
  vi.clearAllMocks();
  refreshed = 0;
  mockGetLogo.mockResolvedValue({ blob: new Blob(["png"]), filename: null });
  vi.stubGlobal("URL", {
    ...URL,
    createObjectURL: vi.fn(() => "blob:test/logo"),
    revokeObjectURL: vi.fn(),
  });
});

describe("SettingsPage loading", () => {
  it("shows a loading note, then the form seeded from the payload", async () => {
    mockGetSettings.mockResolvedValue(SETTINGS({ firstDayOfWeek: "Monday", dateFormatOverride: "dd/MM/yyyy" }));
    render(<SettingsPage />);
    expect(screen.getByText("Loading…")).toBeInTheDocument();

    expect(await screen.findByLabelText("Farm name")).toHaveValue("Hen House");
    expect(screen.getByLabelText("Timezone")).toHaveValue("America/Los_Angeles");
    expect(screen.getByLabelText("Locale")).toHaveValue("en-US");
    expect(screen.getByLabelText("Currency")).toHaveValue("USD");
    expect(screen.getByLabelText("Unit system")).toHaveValue("Metric");
    expect(screen.getByLabelText("First day of week")).toHaveValue("Monday");
    expect(screen.getByLabelText("Date format")).toHaveValue("dd/MM/yyyy");
    // A null override is an empty field, not the string "null".
    expect(screen.getByLabelText("Time format")).toHaveValue("");
  });

  it("reports a failed load instead of an empty form", async () => {
    mockGetSettings.mockRejectedValue(new Error("offline"));
    render(<SettingsPage />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Could not load farm settings.");
    expect(screen.queryByLabelText("Farm name")).not.toBeInTheDocument();
  });
});

describe("SettingsPage saving", () => {
  it("sends every field, the base version, and an idempotency key", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();

    fireEvent.change(screen.getByLabelText("Farm name"), { target: { value: "Coop Co" } });
    fireEvent.change(screen.getByLabelText("Unit system"), { target: { value: "Imperial" } });
    fireEvent.change(screen.getByLabelText("First day of week"), { target: { value: "Sunday" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    expect(mockUpdate).toHaveBeenCalledTimes(1);
    const [body, key] = mockUpdate.mock.calls[0];
    expect(body).toEqual({
      name: "Coop Co",
      timeZoneId: "America/Los_Angeles",
      locale: "en-US",
      currencyCode: "USD",
      unitSystem: "Imperial",
      firstDayOfWeek: "Sunday",
      dateFormatOverride: null,
      timeFormatOverride: null,
      version: 7,
    });
    expect(key).toBeTruthy();
    expect(screen.getByRole("status")).toHaveTextContent("Settings saved.");
  });

  it("sends a blank override as null, not an empty string", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(SETTINGS({ firstDayOfWeek: "Monday", dateFormatOverride: "dd/MM/yyyy" }));

    fireEvent.change(screen.getByLabelText("First day of week"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Date format"), { target: { value: "   " } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    const [body] = mockUpdate.mock.calls[0];
    expect(body.firstDayOfWeek).toBeNull();
    expect(body.dateFormatOverride).toBeNull();
  });

  it("uppercases the currency and trims the text fields", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();

    fireEvent.change(screen.getByLabelText("Currency"), { target: { value: "eur" } });
    fireEvent.change(screen.getByLabelText("Farm name"), { target: { value: "  Coop Co  " } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    const [body] = mockUpdate.mock.calls[0];
    expect(body.currencyCode).toBe("EUR");
    expect(body.name).toBe("Coop Co");
  });

  it("re-reads the settings and refreshes the shell, so the change shows without a reload", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();
    mockGetSettings.mockResolvedValue(SETTINGS({ name: "Coop Co", version: 8 }));

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // The new version is what the NEXT save must carry — a screen still holding
    // 7 would 409 against its own write.
    expect(await screen.findByLabelText("Farm name")).toHaveValue("Coop Co");
    expect(refreshed).toBe(1);
  });

  it("carries a fresh idempotency key into the second save", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // Reusing the key would make the server replay the first response instead
    // of performing the second write.
    const [, first] = mockUpdate.mock.calls[0];
    const [, second] = mockUpdate.mock.calls[1];
    expect(first).not.toBe(second);
  });

  it("explains a 409 as someone else's save, not as a validation error", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Account.VersionMismatch", "Version mismatch."));
    await renderReady();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    expect(screen.getByRole("alert")).toHaveTextContent(/Someone else changed these settings/i);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("surfaces the server's message for any other refusal", async () => {
    mockUpdate.mockRejectedValue(
      new ApiError(422, "Account.CurrencyLocked", "This farm has already recorded amounts in USD."));
    await renderReady();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });
    expect(screen.getByRole("alert")).toHaveTextContent("This farm has already recorded amounts in USD.");
  });
});

describe("SettingsPage currency lock (§4.6)", () => {
  it("leaves the field editable while the farm has recorded nothing", async () => {
    await renderReady(SETTINGS({}, true));
    expect(screen.getByLabelText("Currency")).toBeEnabled();
    expect(screen.queryByText(/The currency is fixed at/)).not.toBeInTheDocument();
  });

  it("disables it with the reason once amounts exist — before the user meets the 422", async () => {
    await renderReady(SETTINGS({}, false));
    expect(screen.getByLabelText("Currency")).toBeDisabled();
    expect(screen.getByText(/The currency is fixed at USD/)).toBeInTheDocument();
  });
});

describe("SettingsPage logo", () => {
  it("says there is none, offers upload only, and never fetches bytes", async () => {
    await renderReady(SETTINGS({ logoContentHash: null }));
    expect(screen.getByText(/No logo set/)).toBeInTheDocument();
    expect(screen.getByText("Upload a logo")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Remove/ })).not.toBeInTheDocument();
    expect(mockGetLogo).not.toHaveBeenCalled();
  });

  it("previews the stored logo and offers replace + remove", async () => {
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));
    const img = await screen.findByAltText("Current farm logo");
    expect(img).toHaveAttribute("src", "blob:test/logo");
    expect(screen.getByText("Replace the logo")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Remove/ })).toBeInTheDocument();
  });

  it("uploads the chosen file, then re-reads and refreshes", async () => {
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "newhash", width: 64, height: 64,
      byteLength: 900, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));

    const input = screen.getByLabelText("Upload a logo");
    const file = imageOfSize(900);
    await act(async () => { fireEvent.change(input, { target: { files: [file] } }); });

    expect(mockUpload).toHaveBeenCalledTimes(1);
    expect(mockUpload.mock.calls[0][0]).toBe(file);
    expect(mockUpload.mock.calls[0][1]).toBeTruthy();
    expect(refreshed).toBe(1);
  });

  it("refuses an oversize file locally, without spending the upload", async () => {
    await renderReady(SETTINGS({ logoContentHash: null }));

    const input = screen.getByLabelText("Upload a logo");
    await act(async () => {
      fireEvent.change(input, { target: { files: [imageOfSize(LOGO_MAX_BYTES + 1)] } });
    });

    expect(mockUpload).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent(/The limit is 1024 KB/);
  });

  it("sends a file exactly at the cap — the guard is > not >=", async () => {
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "h", width: 1, height: 1,
      byteLength: LOGO_MAX_BYTES, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(LOGO_MAX_BYTES)] } });
    });

    expect(mockUpload).toHaveBeenCalledTimes(1);
  });

  it("surfaces a server refusal (415 on an SVG that slipped past the picker)", async () => {
    mockUpload.mockRejectedValue(
      new ApiError(415, "FarmLogo.UnsupportedFormat", "Image must be PNG, JPEG or WebP."));
    await renderReady(SETTINGS({ logoContentHash: null }));

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(400, "logo.svg", "image/svg+xml")] } });
    });

    expect(screen.getByRole("alert")).toHaveTextContent("Image must be PNG, JPEG or WebP.");
  });

  it("removes the logo after the confirm is accepted", async () => {
    mockRemove.mockResolvedValue(undefined);
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });

    expect(mockRemove).toHaveBeenCalledTimes(1);
    expect(refreshed).toBe(1);
  });

  it("does nothing when the confirm is dismissed", async () => {
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    });

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(mockRemove).not.toHaveBeenCalled();
    expect(refreshed).toBe(0);
  });
});
