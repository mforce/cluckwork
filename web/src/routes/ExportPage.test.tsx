import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { ExportPage } from "./ExportPage";
import { EXPORT_DATASETS, downloadExportCsv, downloadFullBackup } from "../api/cluckwork";
import { ApiError } from "../api/client";

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
  global.URL.createObjectURL = vi.fn(() => "blob:mock");
  global.URL.revokeObjectURL = vi.fn();
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
    expect(global.URL.createObjectURL).toHaveBeenCalledTimes(1);
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
    expect(mockCsv).not.toHaveBeenCalled();
    expect(global.URL.createObjectURL).toHaveBeenCalledTimes(1);
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
    expect(global.URL.createObjectURL).not.toHaveBeenCalled();
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

    // The in-flight backup button flips to "Preparing…" and sibling dataset
    // buttons are disabled so no second download can start.
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
