import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, fireEvent, act, waitFor } from "@testing-library/react";
import { SettingsPage, formatByteCap } from "./SettingsPage";
import { FarmContext } from "../farm/FarmContext";
import {
  getFarmLogo, getFarmSettings, listEggUnitConversions, removeFarmLogo, updateFarmSettings,
  uploadFarmLogo,
} from "../api/cluckwork";
import type { Account, FarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { account, farmState } from "../test/fixtures";
import { BRANDS } from "../lib/brand";
import type { Brand } from "../lib/brand";
import i18n from "../i18n";

// Mirrors PALETTE_LABEL_KEYS in SettingsPage.tsx — kept local to the test
// rather than imported, since the production module no longer exports a
// label lookup (the labels themselves live in the `settings` i18n catalog,
// see en.ts). Under the default "en" lng these ARE the rendered text, same
// as every other functional assertion below that checks plain English; the
// dedicated "i18n wiring" describe block further down proves the render
// actually reads the catalog rather than a hardcoded literal.
const PALETTE_NAMES: Record<Brand, string> = {
  aubergine: "Aubergine",
  forest: "Forest",
  slate: "Slate",
  terracotta: "Terracotta",
};

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return {
    ...actual,
    getFarmSettings: vi.fn(),
    updateFarmSettings: vi.fn(),
    uploadFarmLogo: vi.fn(),
    removeFarmLogo: vi.fn(),
    getFarmLogo: vi.fn(),
    listEggUnitConversions: vi.fn(),
  };
});

const mockGetSettings = vi.mocked(getFarmSettings);
const mockUpdate = vi.mocked(updateFarmSettings);
const mockUpload = vi.mocked(uploadFarmLogo);
const mockRemove = vi.mocked(removeFarmLogo);
const mockGetLogo = vi.mocked(getFarmLogo);
const mockListConversions = vi.mocked(listEggUnitConversions);

// #444 — the seeded defaults every real account carries (EggUnitConversion.Defaults).
const CONVERSIONS = [
  { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
  { id: "c2", unitCode: "Dozen", eggsPerUnit: 12, active: true, version: 0 },
  { id: "c3", unitCode: "Tray", eggsPerUnit: 30, active: true, version: 0 },
];

// A fixed cap for the size-boundary tests, independent of the production
// default — the point is the > vs >= behaviour, not the number.
const MAX_UPLOAD = 2 * 1024 * 1024;

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
  logoMaxUploadBytes: MAX_UPLOAD,
});

let refreshed = 0;
let refreshOk = true;

async function renderReady(payload: FarmSettings = SETTINGS()) {
  mockGetSettings.mockResolvedValue(payload);
  const value = farmState({
    farm: payload.settings,
    refresh: async () => { refreshed += 1; return refreshOk; },
  });
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
  refreshOk = true;
  document.documentElement.removeAttribute("data-brand");
  mockGetLogo.mockResolvedValue({ blob: new Blob(["png"]), filename: null });
  mockListConversions.mockResolvedValue(CONVERSIONS);
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
      brand: "aubergine",
      defaultStepperUnit: "Individual",
      version: 7,
    });
    expect(key).toBeTruthy();
    expect(screen.getByText("Settings saved.")).toBeInTheDocument();
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

  it("re-uses the key when the SAME payload is retried after a failure", async () => {
    await renderReady();
    mockUpdate.mockRejectedValueOnce(new Error("connection lost"));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    mockUpdate.mockResolvedValueOnce(undefined);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // The write may have landed before the response was lost — the server has
    // to dedupe, not write again.
    expect(mockUpdate.mock.calls[0][1]).toBe(mockUpdate.mock.calls[1][1]);
  });

  it("takes a NEW key when the payload changed after a failure", async () => {
    await renderReady();
    mockUpdate.mockRejectedValueOnce(new Error("connection lost"));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    fireEvent.change(screen.getByLabelText("Farm name"), { target: { value: "Coop Co" } });
    mockUpdate.mockResolvedValueOnce(undefined);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // Re-using it would replay the FIRST payload's stored response: the screen
    // reports success and the new name is never written (review of #123).
    expect(mockUpdate.mock.calls[0][1]).not.toBe(mockUpdate.mock.calls[1][1]);
    expect(mockUpdate.mock.calls[1][0].name).toBe("Coop Co");
  });

  it("reports a failed read-back as a REFRESH failure, and refuses to save again", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();
    mockGetSettings.mockRejectedValueOnce(new Error("offline"));

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // Calling this "could not save" is how a user makes the same change twice —
    // and the screen still holds the OLD version, so a second save would 409
    // and blame someone else for this user's own write.
    expect(screen.getByRole("alert")).toHaveTextContent(/Saved\. This screen could not read the settings back/);
    expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();
    // Reverting the `saveError === null` guard would leave "Settings saved."
    // sitting under an error that says otherwise.
    expect(screen.queryByText("Settings saved.")).not.toBeInTheDocument();
  });

  it("says the change may not have reached the rest of the app when /account fails", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();
    refreshOk = false;

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    // refresh() cannot throw — the provider has to survive a failed read — so
    // the save has to ASK. Relying on a throw left a timezone change reported
    // as fully applied while the shell still held the old zone.
    expect(screen.getByRole("alert"))
      .toHaveTextContent(/could not pick the change up/);
  });

  it("keeps the save and the logo writes out of each other's way", async () => {
    // The save still issues its own GET. A delayed one landing after a logo
    // write would restore the hash the logo write had just replaced.
    let finishUpload: (() => void) | undefined;
    mockUpload.mockReturnValue(new Promise((resolve) => {
      finishUpload = () => resolve({
        contentType: "image/png", contentHash: "h", width: 1, height: 1,
        byteLength: 10, updatedAt: "2026-07-23T00:00:00Z",
      });
    }));
    await renderReady(SETTINGS({ logoContentHash: null }));

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(10)] } });
    });
    expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();

    await act(async () => { finishUpload!(); });
    expect(screen.getByRole("button", { name: "Save settings" })).toBeEnabled();
  });

  it("warns when the timezone is one this browser cannot format", async () => {
    await renderReady();
    fireEvent.change(screen.getByLabelText("Timezone"), { target: { value: "Mars/Olympus_Mons" } });

    // The server validates against ITS tzdata. A zone it accepts but the
    // browser cannot format saves fine and then sends every date field back to
    // the device's day — silently, without this.
    expect(screen.getByText(/does not know that timezone/)).toBeInTheDocument();
    const tz = screen.getByLabelText("Timezone");
    const describedBy = tz.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(/does not know that timezone/);
  });

  it("says nothing about a timezone the browser does know", async () => {
    await renderReady();
    fireEvent.change(screen.getByLabelText("Timezone"), { target: { value: "Asia/Tokyo" } });
    expect(screen.queryByText(/does not know that timezone/)).not.toBeInTheDocument();
  });

  it("explains a 409 as someone else's save, not as a validation error", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Account.VersionMismatch", "Version mismatch."));
    await renderReady();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    expect(screen.getByRole("alert")).toHaveTextContent(/Someone else changed these settings/i);
    expect(screen.queryByText("Settings saved.")).not.toBeInTheDocument();
    // The message says reload. A still-enabled button says try again — and a
    // retry sends the same version, so it 409s forever (the middleware caches
    // only 2xx, so nothing is replayed).
    expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();
  });

  it("surfaces the server's message for any other refusal", async () => {
    mockUpdate.mockRejectedValue(
      new ApiError(422, "Account.CurrencyLocked", "This farm has already recorded amounts in USD."));
    await renderReady();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });
    expect(screen.getByRole("alert")).toHaveTextContent("This farm has already recorded amounts in USD.");
  });
});

// #444 — the farm-default Daily Entry stepper pack unit.
describe("SettingsPage stepper unit (#444)", () => {
  const select = () => screen.getByLabelText("Daily Entry counting unit");

  it("offers only the ACTIVE conversions and selects the stored default", async () => {
    mockListConversions.mockResolvedValue([
      ...CONVERSIONS,
      { id: "c4", unitCode: "Case", eggsPerUnit: 360, active: false, version: 0 },
    ]);
    await renderReady(SETTINGS({ defaultStepperUnit: "Tray" }));

    expect(select()).toHaveValue("Tray");
    const options = within(select()).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Individual", "Dozen", "Tray"]); // no inactive Case
  });

  it("sends the picked unit on save", async () => {
    mockUpdate.mockResolvedValue(undefined);
    await renderReady();

    fireEvent.change(select(), { target: { value: "Tray" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    expect(mockUpdate.mock.calls[0][0]).toMatchObject({ defaultStepperUnit: "Tray" });
  });

  it("falls back to Individual when the stored default's conversion is inactive", async () => {
    // Same recovery as a retired brand: echoing a deactivated unit straight
    // back on the next save would 422.
    mockListConversions.mockResolvedValue([
      { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
      { id: "c3", unitCode: "Tray", eggsPerUnit: 30, active: false, version: 0 },
    ]);
    await renderReady(SETTINGS({ defaultStepperUnit: "Tray" }));

    expect(select()).toHaveValue("Individual");
  });
});

describe("SettingsPage palette (#149)", () => {
  it("renders a swatch for every curated palette, with the current one selected", async () => {
    await renderReady(SETTINGS({ brand: "forest" }));

    for (const id of BRANDS)
      expect(screen.getByRole("radio", { name: PALETTE_NAMES[id] })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Forest" })).toBeChecked();
  });

  it("sends the selected palette on save", async () => {
    await renderReady(SETTINGS({ brand: "aubergine" }));
    mockUpdate.mockResolvedValue(undefined);

    fireEvent.click(screen.getByRole("radio", { name: "Slate" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
    });

    expect(mockUpdate).toHaveBeenCalledTimes(1);
    expect(mockUpdate.mock.calls[0][0]).toMatchObject({ brand: "slate" });
  });

  it("does not change the palette before the save lands", async () => {
    // The issue asks for apply-on-save, not a live preview.
    await renderReady(SETTINGS({ brand: "aubergine" }));

    fireEvent.click(screen.getByRole("radio", { name: "Slate" }));

    expect(document.documentElement.dataset.brand).toBeUndefined();
  });

  it("applies the palette from its own re-read even when the shell refresh fails", async () => {
    // The save path is PUT -> re-read settings -> refresh(). refresh() cannot
    // throw, it reports; if only FarmProvider applied the brand, a successful
    // save with a failed refresh would leave the OLD palette live and cached
    // while the authoritative new value was already in hand.
    await renderReady(SETTINGS({ brand: "aubergine" }));
    mockUpdate.mockResolvedValue(undefined);
    // renderReady already primed the first GET; the post-save re-read returns
    // the saved palette.
    mockGetSettings.mockResolvedValue(SETTINGS({ brand: "slate" }));
    refreshOk = false; // the shell refresh reports failure

    fireEvent.click(screen.getByRole("radio", { name: "Slate" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
    });

    expect(document.documentElement.dataset.brand).toBe("slate");
  });

  it("falls back to the default when the farm holds a retired palette", async () => {
    // A palette can be retired while farms still reference it. Echoing the
    // stored id back on the next save would 422; the screen shows the default
    // selected instead, so saving writes a curated value.
    await renderReady(SETTINGS({ brand: "chartreuse" }));

    expect(screen.getByRole("radio", { name: "Aubergine" })).toBeChecked();
  });
});

describe("SettingsPage currency lock (§4.6)", () => {
  it("leaves the field editable while the farm has recorded nothing", async () => {
    await renderReady(SETTINGS({}, true));
    const currency = screen.getByLabelText("Currency");
    expect(currency).not.toHaveAttribute("readonly");
    expect(currency).not.toHaveClass("locked");
    expect(currency).not.toHaveAttribute("aria-describedby");
    expect(screen.queryByText(/currency is fixed at/i)).not.toBeInTheDocument();
  });

  it("locks it with the reason once amounts exist — before the user meets the 422", async () => {
    await renderReady(SETTINGS({}, false));
    const currency = screen.getByLabelText("Currency");
    // readOnly rather than disabled: a disabled control leaves the tab order,
    // taking the explanation with it.
    expect(currency).toHaveAttribute("readonly");
    expect(currency).not.toBeDisabled();
    // The locked LOOK hangs off this class, not off `input:read-only` — that
    // pseudo-class also matches every checkbox, radio and file input in the
    // app, which the blanket rule greyed out (round 2: codex + agent).
    expect(currency).toHaveClass("locked");
    expect(screen.getByText(/The currency is fixed at USD/)).toBeInTheDocument();
  });

  it("names the field 'Currency' and carries the reason as a DESCRIPTION", async () => {
    await renderReady(SETTINGS({}, false));
    const currency = screen.getByLabelText("Currency");
    // A note nested inside the <label> would join the accessible name, so the
    // field would announce itself as "Currency The currency is fixed at USD…".
    const describedBy = currency.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(/fixed at USD/);
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

  it("tells the admin a square, simple mark reads best in the sidebar", async () => {
    await renderReady(SETTINGS({ logoContentHash: null }));
    // The guidance discovered through use: the slot is small and square, so a
    // detailed or wide logo shrinks to mush.
    expect(screen.getByText(/Use a/)).toHaveTextContent(/square/);
    expect(screen.getByText(/reads far better there/)).toBeInTheDocument();
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
      fireEvent.change(input, { target: { files: [imageOfSize(MAX_UPLOAD + 1)] } });
    });

    expect(mockUpload).not.toHaveBeenCalled();
    // The limit in the message is the server's, from the payload (2048 KB), not
    // a client constant.
    expect(screen.getByRole("alert")).toHaveTextContent(/The limit is 2048 KB/);
  });

  it("takes the upload cap from the server payload, not a hardcoded constant", async () => {
    // A farm configured BELOW the default: both the copy and the pre-check must
    // follow the payload, or a config change silently diverges from what the
    // client enforces (#123).
    await renderReady({ ...SETTINGS({ logoContentHash: null }), logoMaxUploadBytes: 512 * 1024 });

    // The stated cap reflects the payload...
    expect(screen.getByText(/up to 512 KB/)).toBeInTheDocument();

    // ...and so does the refusal: a 600 KB file is over THIS farm's 512 KB.
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(600 * 1024)] } });
    });
    expect(mockUpload).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent(/The limit is 512 KB/);
  });

  it("sends a file exactly at the cap — the guard is > not >=", async () => {
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "h", width: 1, height: 1,
      byteLength: MAX_UPLOAD, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(MAX_UPLOAD)] } });
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

  it("keeps unsaved form edits when a logo is uploaded", async () => {
    // The whole reason the logo write no longer re-reads the settings: load()
    // re-seeds every field, so an admin who types a new name and THEN uploads
    // a logo would watch the name revert with no message (review of #123).
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "newhash", width: 64, height: 64,
      byteLength: 900, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));

    fireEvent.change(screen.getByLabelText("Farm name"), { target: { value: "Coop Co" } });
    fireEvent.change(screen.getByLabelText("Locale"), { target: { value: "es-MX" } });
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(900)] } });
    });

    expect(screen.getByLabelText("Farm name")).toHaveValue("Coop Co");
    expect(screen.getByLabelText("Locale")).toHaveValue("es-MX");
    // And no second read to land out of order with a save's.
    expect(mockGetSettings).toHaveBeenCalledTimes(1);
  });

  it("keeps unsaved form edits when the logo is removed", async () => {
    mockRemove.mockResolvedValue(undefined);
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    fireEvent.change(screen.getByLabelText("Farm name"), { target: { value: "Coop Co" } });
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });

    expect(screen.getByLabelText("Farm name")).toHaveValue("Coop Co");
    expect(mockGetSettings).toHaveBeenCalledTimes(1);
  });

  it("takes the new hash from the upload response, so the preview follows", async () => {
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "newhash", width: 64, height: 64,
      byteLength: 900, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));
    expect(mockGetLogo).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(900)] } });
    });

    // No re-read of the settings, so this is the response's own hash driving
    // the fetch — the panel now offers Replace/Remove and pulls the bytes.
    expect(await screen.findByAltText("Current farm logo")).toBeInTheDocument();
    expect(mockGetLogo).toHaveBeenCalledTimes(1);

    // WHICH hash, not merely that one was applied: patching a constant would
    // pass everything above, and then a REPLACEMENT would never re-fetch.
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "secondhash", width: 64, height: 64,
      byteLength: 800, updatedAt: "2026-07-23T00:01:00Z",
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Replace the logo"),
        { target: { files: [imageOfSize(800, "two.png")] } });
    });
    expect(mockGetLogo).toHaveBeenCalledTimes(2);
  });

  // Both uploads succeed here, so the key is cleared between them and the
  // pre-fix rotate-on-success code passes this too. It documents the everyday
  // shape; the discriminating case is the failure one below.
  it("gives two different files two different idempotency keys", async () => {
    mockUpload.mockResolvedValue({
      contentType: "image/png", contentHash: "h", width: 1, height: 1,
      byteLength: 10, updatedAt: "2026-07-23T00:00:00Z",
    });
    await renderReady(SETTINGS({ logoContentHash: null }));
    const input = screen.getByLabelText("Upload a logo");

    await act(async () => {
      fireEvent.change(input, { target: { files: [imageOfSize(10, "one.png")] } });
    });
    await act(async () => {
      fireEvent.change(input, { target: { files: [imageOfSize(20, "two.png")] } });
    });

    // Sharing a key would make the server replay the FIRST image's response for
    // the second upload — reported as success, with image one still stored.
    expect(mockUpload.mock.calls[0][1]).not.toBe(mockUpload.mock.calls[1][1]);
  });

  it("takes a NEW key when a DIFFERENT file is picked after a failure", async () => {
    // The whole point of binding the key to the payload. With the key kept
    // across a payload change, the server replays the FIRST image's stored
    // response: the upload reports success and image one is still what is
    // stored (review of #123). The success-path test above cannot see this —
    // the key is cleared on success either way.
    await renderReady(SETTINGS({ logoContentHash: null }));
    const input = screen.getByLabelText("Upload a logo");

    mockUpload.mockRejectedValueOnce(new Error("connection lost"));
    await act(async () => {
      fireEvent.change(input, { target: { files: [imageOfSize(900, "one.png")] } });
    });
    mockUpload.mockResolvedValueOnce({
      contentType: "image/png", contentHash: "h", width: 1, height: 1,
      byteLength: 900, updatedAt: "2026-07-23T00:00:00Z",
    });
    await act(async () => {
      fireEvent.change(input, { target: { files: [imageOfSize(950, "two.png")] } });
    });

    expect(mockUpload.mock.calls[0][1]).not.toBe(mockUpload.mock.calls[1][1]);
  });

  it("re-uses the key when the SAME file is retried after a failure", async () => {
    await renderReady(SETTINGS({ logoContentHash: null }));
    const input = screen.getByLabelText("Upload a logo");
    const file = imageOfSize(900, "one.png");

    mockUpload.mockRejectedValueOnce(new Error("connection lost"));
    await act(async () => { fireEvent.change(input, { target: { files: [file] } }); });
    mockUpload.mockResolvedValueOnce({
      contentType: "image/png", contentHash: "h", width: 1, height: 1,
      byteLength: 900, updatedAt: "2026-07-23T00:00:00Z",
    });
    await act(async () => { fireEvent.change(input, { target: { files: [file] } }); });

    // The point of the key: the first attempt may have landed before the
    // response was lost, and the server must dedupe rather than store twice.
    expect(mockUpload.mock.calls[0][1]).toBe(mockUpload.mock.calls[1][1]);
  });

  it("binds the removal key to the logo it is removing", async () => {
    // The discriminating sequence, and it needs a FAILED removal: on success
    // the key is cleared, so a bare "remove" payload mints a fresh one anyway
    // and nothing is visible. Here the first removal fails, so its key is still
    // held when the logo underneath changes.
    mockUpdate.mockResolvedValue(undefined);
    await renderReady(SETTINGS({ logoContentHash: "first" }));

    mockRemove.mockRejectedValueOnce(new Error("connection lost"));
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });

    // Another admin replaced the logo meanwhile; this screen learns of it on
    // the read-back after its own save.
    mockGetSettings.mockResolvedValue(SETTINGS({ logoContentHash: "second", version: 8 }));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });

    mockRemove.mockResolvedValueOnce(undefined);
    fireEvent.click(await screen.findByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });

    expect(mockRemove).toHaveBeenCalledTimes(2);
    // A shared "remove" payload reuses the first key, so the server replays the
    // 204 that deleted the FIRST logo and the second one quietly survives while
    // the screen reports it gone (codex round 2).
    expect(mockRemove.mock.calls[0][0]).not.toBe(mockRemove.mock.calls[1][0]);
  });

  it("announces the result of a logo write", async () => {
    mockRemove.mockResolvedValue(undefined);
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });

    // Otherwise the only signal is an <img> disappearing, which a screen
    // reader does not see.
    expect(screen.getByText("Logo removed.")).toBeInTheDocument();
  });

  it("moves focus off the Remove button it just destroyed", async () => {
    // DEFERRED, not mockResolvedValue: with an immediately-resolved promise the
    // whole handler runs inside one act() flush, so React never commits
    // `logoBusy` and the upload input is never actually disabled. The old test
    // passed for that reason alone and could not fail for the real one — in a
    // browser the round trip gives React ample time to disable the input, and
    // focus() on a disabled control is a silent no-op (round 2: two agents).
    let finishRemove: (() => void) | undefined;
    mockRemove.mockReturnValue(new Promise<void>((resolve) => {
      finishRemove = () => resolve();
    }));
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
    });
    // The busy state is committed now: the input really is disabled.
    expect(screen.getByLabelText("Replace the logo")).toBeDisabled();

    await act(async () => { finishRemove!(); });

    // Dialog restores focus only to a trigger still in the document, and the
    // Remove button unmounts with the logo — so without this the keyboard user
    // is dumped on <body> at the top of the page.
    const upload = screen.getByLabelText("Upload a logo");
    expect(upload).toBeEnabled();
    expect(document.activeElement).toBe(upload);
  });

  it("says the logo is loading, not that there is none", async () => {
    let resolve: ((value: { blob: Blob; filename: string | null }) => void) | undefined;
    mockGetLogo.mockReturnValue(new Promise((r) => { resolve = r; }));
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    // "No logo set" beside a Remove button is a contradiction the reader has
    // no way to resolve.
    expect(screen.getByText("Loading the logo…")).toBeInTheDocument();
    expect(screen.queryByText(/No logo set/)).not.toBeInTheDocument();

    await act(async () => { resolve!({ blob: new Blob(["png"]), filename: null }); });
    expect(await screen.findByAltText("Current farm logo")).toBeInTheDocument();
  });

  it("says the logo could not be loaded, not that there is none", async () => {
    mockGetLogo.mockRejectedValue(new Error("500"));
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    expect(await screen.findByText("The logo could not be loaded.")).toBeInTheDocument();
    expect(screen.queryByText(/No logo set/)).not.toBeInTheDocument();
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

// #236 — one usePendingAction now carries what `saving` + `logoBusy` used to,
// under the scopes "settings", "logo:upload" and "logo:remove". Mutual
// exclusion (everything disables) is unchanged; what these pin is that each
// surface reflects only ITS OWN scope: a logo flight must not make the Save
// button claim to be working, and a settings save must not make the logo
// region say so.
describe("SettingsPage pending scopes (#236)", () => {
  it("keeps the settings Save merely disabled — not spinning — while a logo upload is in flight", async () => {
    let finishUpload!: () => void;
    mockUpload.mockReturnValue(new Promise((resolve) => {
      finishUpload = () => resolve({
        contentType: "image/png", contentHash: "h", width: 1, height: 1,
        byteLength: 10, updatedAt: "2026-07-27T00:00:00Z",
      });
    }));
    await renderReady(SETTINGS({ logoContentHash: null }));

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Upload a logo"),
        { target: { files: [imageOfSize(10)] } });
    });

    // Disabled by the shared flight, but NOT aria-busy: the save is not the
    // thing that is working.
    const save = screen.getByRole("button", { name: "Save settings" });
    expect(save).toBeDisabled();
    expect(save).not.toHaveAttribute("aria-busy");
    // The palette radios bind to the settings scope only — a logo flight
    // leaves them alone.
    expect(screen.getByRole("radio", { name: "Aubergine" })).toBeEnabled();
    // The logo's own status region (first p.success in the document) carries
    // the announcement, exactly as before the consolidation.
    expect(document.querySelector("p.success")).toHaveTextContent("Working…");
    expect(screen.getByLabelText("Upload a logo")).toBeDisabled();

    await act(async () => { finishUpload(); });
    expect(screen.getByRole("button", { name: "Save settings" })).toBeEnabled();
    expect(document.querySelector("p.success")).toHaveTextContent("Logo updated.");
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });

  it("spins the Save for a settings flight while the logo surfaces stay silent and the radios lock", async () => {
    let finishSave!: () => void;
    mockUpdate.mockReturnValue(new Promise<void>((resolve) => { finishSave = resolve; }));
    await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
    });

    // The label swap stays the caller's (BusyButton passes children through).
    const save = screen.getByRole("button", { name: "Saving…" });
    expect(save).toHaveAttribute("aria-busy", "true");
    expect(save).toBeDisabled();
    expect(screen.getByRole("radio", { name: "Aubergine" })).toBeDisabled();
    // Logo controls disable through the shared flight but never claim to work.
    const remove = screen.getByRole("button", { name: /Remove/ });
    expect(remove).toBeDisabled();
    expect(remove).not.toHaveAttribute("aria-busy");
    expect(screen.getByLabelText("Replace the logo")).toBeDisabled();
    expect(document.querySelector("p.success")?.textContent).toBe("");

    await act(async () => { finishSave(); });
    expect(screen.getByRole("button", { name: "Save settings" })).toBeEnabled();
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 21, batch B4)
// ---------------------------------------------------------------------------

// `settings` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("SettingsPage i18n wiring (#182, Task 21)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("settings", "heading", "HEADING-MARKER", async () => {
      await renderReady();
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Farm settings" })).not.toBeInTheDocument();
    });
  });

  it("reads the remove-logo button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("settings", "removeLogoButton", "REMOVE-MARKER", async () => {
      await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));
      expect(screen.getByRole("button", { name: /REMOVE-MARKER/ })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /^Remove$/ })).not.toBeInTheDocument();
    });
  });

  it("interpolates the byte cap into the logo rules hint from the catalog", async () => {
    await withOverride(
      "settings", "logoRulesHint", "RULES-MARKER {{cap}} MARKER-END",
      async () => {
        await renderReady();
        expect(screen.getByText("RULES-MARKER 2 MB MARKER-END")).toBeInTheDocument();
        expect(screen.queryByText(/up to 2 MB and 4096/)).not.toBeInTheDocument();
      },
    );
  });

  it("reads the JSX-interleaved square-logo hint from the catalog via <Trans>", async () => {
    await withOverride("settings", "logoSquareHint", "SQUARE-HINT-MARKER", async () => {
      await renderReady();
      expect(screen.getByText("SQUARE-HINT-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/reads far better there/)).not.toBeInTheDocument();
    });
  });

  it("interpolates the locked currency code into the note from the catalog", async () => {
    await withOverride(
      "settings", "currencyLockedNote", "LOCKED-MARKER {{code}} MARKER-END",
      async () => {
        await renderReady(SETTINGS({}, false));
        expect(screen.getByText("LOCKED-MARKER USD MARKER-END")).toBeInTheDocument();
        expect(screen.queryByText(/The currency is fixed at USD/)).not.toBeInTheDocument();
      },
    );
  });

  // Proves the Unit system SELECT's option text reads the `unitSystem` ENUM
  // label from the catalog (via unitSystemLabel) — not a hardcoded literal
  // and not the raw wire value coincidentally matching it.
  it("reads the unit-system option labels from the enums catalog, not a hardcoded literal", async () => {
    await withOverride("enums", "unitSystem.Metric", "METRIC-MARKER", async () => {
      await renderReady();
      const select = screen.getByLabelText("Unit system");
      expect(within(select).getByRole("option", { name: "METRIC-MARKER" })).toBeInTheDocument();
      expect(within(select).queryByRole("option", { name: "Metric" })).not.toBeInTheDocument();
    });
  });

  // Same proof for the First-day-of-week SELECT's `weekday` ENUM label (via
  // weekdayLabel).
  it("reads the first-day-of-week option labels from the enums catalog, not a hardcoded literal", async () => {
    await withOverride("enums", "weekday.Monday", "MONDAY-MARKER", async () => {
      await renderReady();
      const select = screen.getByLabelText("First day of week");
      expect(within(select).getByRole("option", { name: "MONDAY-MARKER" })).toBeInTheDocument();
      expect(within(select).queryByRole("option", { name: "Monday" })).not.toBeInTheDocument();
    });
  });

  it("reads a curated-palette display name from the catalog, not a hardcoded literal", async () => {
    await withOverride("settings", "paletteAubergine", "PALETTE-MARKER", async () => {
      await renderReady(SETTINGS({ brand: "aubergine" }));
      expect(screen.getByRole("radio", { name: "PALETTE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("radio", { name: "Aubergine" })).not.toBeInTheDocument();
    });
  });

  // The confirm dialog's title is built with the imperative i18n.t()
  // (onRemoveLogo is an event handler, not render — see
  // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
  it("reads the remove-logo confirm dialog title from the catalog, not a hardcoded literal", async () => {
    await withOverride("settings", "removeLogoConfirmTitle", "CONFIRM-TITLE-MARKER", async () => {
      await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));
      fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
      expect(await screen.findByRole("heading", { name: "CONFIRM-TITLE-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the logo-removed success message from the catalog, not a hardcoded literal", async () => {
    mockRemove.mockResolvedValue(undefined);
    await withOverride("settings", "logoRemovedMessage", "REMOVED-MARKER", async () => {
      await renderReady(SETTINGS({ logoContentHash: "deadbeef" }));
      fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Remove logo" }));
      });
      expect(screen.getByText("REMOVED-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("Logo removed.")).not.toBeInTheDocument();
    });
  });

  it("interpolates the oversize sizes into the logo-too-large message from the catalog", async () => {
    await withOverride(
      "settings", "logoOversizeMessage", "OVERSIZE-MARKER {{actualKb}}/{{limitKb}} MARKER-END",
      async () => {
        await renderReady(SETTINGS({ logoContentHash: null }));
        await act(async () => {
          fireEvent.change(screen.getByLabelText("Upload a logo"),
            { target: { files: [imageOfSize(MAX_UPLOAD + 1024)] } });
        });
        expect(screen.getByRole("alert")).toHaveTextContent("OVERSIZE-MARKER 2049/2048 MARKER-END");
      },
    );
  });

  // The save-error message is built with the imperative i18n.t() (onSave's
  // 409 branch is an event handler, not render).
  it("reads the version-conflict save-error message from the catalog, not a hardcoded literal", async () => {
    mockUpdate.mockRejectedValue(new ApiError(409, "Account.VersionMismatch", "Version mismatch."));
    await withOverride("settings", "versionConflictMessage", "CONFLICT-MARKER", async () => {
      await renderReady();
      await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save settings" })); });
      expect(screen.getByRole("alert")).toHaveTextContent("CONFLICT-MARKER");
      expect(screen.queryByText(/Someone else changed these settings/i)).not.toBeInTheDocument();
    });
  });
});

describe("formatByteCap", () => {
  it("shows a whole number of MB without a decimal", () => {
    expect(formatByteCap(2 * 1024 * 1024)).toBe("2 MB");
    expect(formatByteCap(5 * 1024 * 1024)).toBe("5 MB");
  });

  it("shows one decimal for a fractional MB, never binary-fraction noise", () => {
    expect(formatByteCap(1.5 * 1024 * 1024)).toBe("1.5 MB");
    // 1,000,000 bytes is the case codex named: a plain division prints
    // "0.95367431640625 MB".
    expect(formatByteCap(1_000_000)).toBe("976 KB"); // floored, never overstating the limit
  });

  it("shows KB below 1 MiB", () => {
    expect(formatByteCap(512 * 1024)).toBe("512 KB");
    expect(formatByteCap(64 * 1024)).toBe("64 KB");
    // 1,536 bytes — the other case codex named — is 1 KB floored, not a bogus MB.
    expect(formatByteCap(1536)).toBe("1 KB");
  });
});
