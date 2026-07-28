import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { ExportPage } from "./ExportPage";
import { EXPORT_DATASETS, downloadExportCsv, downloadFullBackup } from "../api/cluckwork";
import { ApiError } from "../api/client";
import i18n from "../i18n";

// Network seam only: stub the two download fns the screen can call, but keep
// EXPORT_DATASETS real (the list the screen maps into buttons) via importOriginal.
// ApiError comes from ../api/client and stays real (errText branches on it).
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return {
    ...actual,
    downloadExportCsv: vi.fn(),
    downloadFullBackup: vi.fn(),
  };
});

const mockCsv = vi.mocked(downloadExportCsv);
const mockBackup = vi.mocked(downloadFullBackup);

// saveBlob() creates an object URL and clicks a hidden <a download>. jsdom has
// neither URL.createObjectURL nor a real anchor-navigation, so stub both. The
// anchor spy also captures the download filename the screen chose (server value
// vs. fallback) so tests can assert it — that name is real behavior.
let anchorClicks: { href: string; download: string }[] = [];
vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
  this: HTMLAnchorElement,
) {
  anchorClicks.push({ href: this.href, download: this.download });
});

const blob = () => new Blob(["data"]);

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  anchorClicks = [];
  globalThis.URL.createObjectURL = vi.fn(() => "blob:mock");
  globalThis.URL.revokeObjectURL = vi.fn();
  // Default: both downloads succeed with no server-provided filename.
  mockCsv.mockResolvedValue({ blob: blob(), filename: null });
  mockBackup.mockResolvedValue({ blob: blob(), filename: null });
});

describe("ExportPage rendering", () => {
  it("renders a download button for every dataset plus the full-backup button", () => {
    render(<ExportPage />);

    expect(
      screen.getByRole("button", { name: "Download full backup (zip)" }),
    ).toBeInTheDocument();
    // Every dataset in the real list is offered as its own button.
    for (const d of EXPORT_DATASETS) {
      expect(screen.getByRole("button", { name: d })).toBeInTheDocument();
    }
    // One button per dataset + the single full-backup button, nothing extra.
    expect(screen.getAllByRole("button")).toHaveLength(EXPORT_DATASETS.length + 1);
  });
});

describe("ExportPage single-dataset download", () => {
  it("downloads the clicked dataset via downloadExportCsv with that dataset key", async () => {
    mockCsv.mockResolvedValue({ blob: blob(), filename: "customers.csv" });
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "customers" }));
    });

    // The ARGUMENT is the behavior: the clicked dataset, and only that one.
    expect(mockCsv).toHaveBeenCalledTimes(1);
    expect(mockCsv).toHaveBeenCalledWith("customers");
    expect(mockBackup).not.toHaveBeenCalled();
    // The blob was handed to saveBlob and the server filename was honoured.
    expect(globalThis.URL.createObjectURL).toHaveBeenCalledTimes(1);
    expect(anchorClicks).toHaveLength(1);
    expect(anchorClicks[0].download).toBe("customers.csv");
  });

  it("falls back to cluckwork-<dataset>.csv when the response carries no filename", async () => {
    // mockCsv default resolves with filename: null → screen supplies the fallback.
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "flocks" }));
    });

    expect(mockCsv).toHaveBeenCalledWith("flocks");
    expect(anchorClicks[0].download).toBe("cluckwork-flocks.csv");
  });
});

describe("ExportPage full backup", () => {
  it("downloads the full backup via downloadFullBackup, not the per-dataset export", async () => {
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Download full backup (zip)" }));
    });

    expect(mockBackup).toHaveBeenCalledTimes(1);
    // downloadFullBackup takes no arguments — the whole account is the payload.
    expect(mockBackup).toHaveBeenCalledWith();
    // The per-dataset export path is never touched for a full backup.
    expect(mockCsv).not.toHaveBeenCalled();
    expect(globalThis.URL.createObjectURL).toHaveBeenCalledTimes(1);
  });

  it("names the backup file cluckwork-backup.zip when the response has no filename", async () => {
    // mockBackup default resolves with filename: null → screen supplies the fallback.
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Download full backup (zip)" }));
    });

    expect(anchorClicks[0].download).toBe("cluckwork-backup.zip");
  });
});

describe("ExportPage errors", () => {
  it("shows an alert with the API error message when a dataset download fails", async () => {
    // Event-handler rejection (handled by the screen's try/catch), not a
    // mount-effect chain — safe to assert. errText surfaces ApiError.message.
    mockCsv.mockRejectedValue(new ApiError(500, "Export failed", "the export ran out of eggs"));
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "egg-grades" }));
    });

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("the export ran out of eggs");
    // A failed fetch never reaches saveBlob.
    expect(globalThis.URL.createObjectURL).not.toHaveBeenCalled();
    expect(anchorClicks).toHaveLength(0);
  });
});

describe("ExportPage busy state", () => {
  it("shows Preparing… and disables every button while a download is in flight", async () => {
    let resolve!: (v: { blob: Blob; filename: string | null }) => void;
    mockBackup.mockReturnValue(
      new Promise<{ blob: Blob; filename: string | null }>((r) => (resolve = r)),
    );
    render(<ExportPage />);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Download full backup (zip)" }));
    });

    // While the backup is in flight EVERY export button is disabled — the
    // button that initiated the download (now "Preparing…") and every dataset
    // button — so no second download can start.
    const buttons = screen.getAllByRole("button");
    expect(buttons).toHaveLength(EXPORT_DATASETS.length + 1);
    for (const b of buttons) {
      expect(b).toBeDisabled();
    }
    // The in-flight backup button flips to "Preparing…"; the rest keep their labels.
    expect(screen.getByRole("button", { name: "Preparing…" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "customers" })).toBeDisabled();

    // Settle the pending download so it doesn't dangle past the test.
    await act(async () => {
      resolve({ blob: blob(), filename: null });
    });
    expect(
      screen.getByRole("button", { name: "Download full backup (zip)" }),
    ).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 30, batch B5)
// ---------------------------------------------------------------------------

// `export` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting plain English under default lng:"en" would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique every prior batch uses, so
// each marker only renders if the screen actually reads the catalog rather
// than a literal that happens to still match it.
describe("ExportPage i18n wiring (#182, Task 30)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "heading", "HEADING-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Export" })).not.toBeInTheDocument();
    });
  });

  it("reads the intro paragraph from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "intro", "INTRO-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByText("INTRO-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Download your account's data/)).not.toBeInTheDocument();
    });
  });

  it("reads the full-backup section heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "fullBackupHeading", "SECTION-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByRole("heading", { name: "SECTION-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Full backup" })).not.toBeInTheDocument();
    });
  });

  it("reads the full-backup button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "fullBackupButton", "BACKUP-BUTTON-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByRole("button", { name: "BACKUP-BUTTON-MARKER" })).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Download full backup (zip)" }),
      ).not.toBeInTheDocument();
    });
  });

  it("reads the full-backup hint from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "fullBackupHint", "HINT-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByText("HINT-MARKER")).toBeInTheDocument();
      expect(
        screen.queryByText(/One zip with every dataset below/),
      ).not.toBeInTheDocument();
    });
  });

  it("reads the single-datasets section heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("export", "singleDatasetsHeading", "DATASETS-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByRole("heading", { name: "DATASETS-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Single datasets" })).not.toBeInTheDocument();
    });
  });

  // Proves `preparingButton` is a SHARED key: overriding it once changes the
  // busy label on BOTH the full-backup button AND a dataset button — the two
  // separate `busy === <key> ? t("preparingButton") : ...` call sites in the
  // component read the same catalog entry rather than each carrying its own
  // hardcoded "Preparing…" literal.
  it("reads the shared preparing label on both the full-backup and a dataset button", async () => {
    let resolveBackup!: (v: { blob: Blob; filename: string | null }) => void;
    mockBackup.mockReturnValue(
      new Promise<{ blob: Blob; filename: string | null }>((r) => (resolveBackup = r)),
    );
    let resolveCsv!: (v: { blob: Blob; filename: string | null }) => void;
    mockCsv.mockReturnValue(
      new Promise<{ blob: Blob; filename: string | null }>((r) => (resolveCsv = r)),
    );

    await withOverride("export", "preparingButton", "BUSY-MARKER", async () => {
      render(<ExportPage />);

      // Full-backup button, busy.
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Download full backup (zip)" }));
      });
      expect(screen.getByRole("button", { name: "BUSY-MARKER" })).toBeInTheDocument();
      expect(screen.queryByText("Preparing…")).not.toBeInTheDocument();
      await act(async () => {
        resolveBackup({ blob: blob(), filename: null });
      });

      // Same tree, a dataset button, busy — same catalog key drives both.
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "flocks" }));
      });
      expect(screen.getByRole("button", { name: "BUSY-MARKER" })).toBeInTheDocument();
      expect(screen.queryByText("Preparing…")).not.toBeInTheDocument();
      await act(async () => {
        resolveCsv({ blob: blob(), filename: null });
      });
    });
  });

  // Per-dataset labels: overriding ONE dataset's key changes only that
  // button, proving each of the 20 EXPORT_DATASETS members has its OWN
  // catalog key ("dataset.<slug>"), not a single shared/derived string.
  it("reads a single dataset's label from its own catalog key, leaving the rest untouched", async () => {
    await withOverride("export", "dataset.customers", "CUSTOMERS-MARKER", async () => {
      render(<ExportPage />);
      expect(await screen.findByRole("button", { name: "CUSTOMERS-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "customers" })).not.toBeInTheDocument();
      // A different dataset's button is untouched by this override.
      expect(screen.getByRole("button", { name: "flocks" })).toBeInTheDocument();
    });
  });

  // Every EXPORT_DATASETS member resolves to a real, non-empty catalog entry
  // — not a missing key silently falling back to i18next's raw-key render.
  it("resolves every EXPORT_DATASETS member to a real catalog key", () => {
    render(<ExportPage />);
    for (const d of EXPORT_DATASETS) {
      expect(i18n.exists(`export:dataset.${d}`)).toBe(true);
    }
  });
});
