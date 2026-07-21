import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { StockPage } from "./StockPage";
import { getStock } from "../api/cluckwork";
import type { StockRow } from "../api/cluckwork";

// Mock the API seam so the screen renders against controlled data — no network,
// no backend. This proves the component test harness handles an async data load,
// the loading/empty branches, and the client-side total.
vi.mock("../api/cluckwork", () => ({
  getStock: vi.fn(),
  listEggLots: vi.fn(),
  listEggLotMovements: vi.fn(),
}));

const mockGetStock = vi.mocked(getStock);

const ROWS: StockRow[] = [
  { eggGradeId: "g1", gradeName: "Grade A", available: 100, restricted: 0 },
  { eggGradeId: "g2", gradeName: "Grade B", available: 50, restricted: 5 },
];

beforeEach(() => mockGetStock.mockReset());

describe("StockPage", () => {
  it("shows a loading state until the stock request resolves", async () => {
    let resolve!: (rows: StockRow[]) => void;
    mockGetStock.mockReturnValue(new Promise<StockRow[]>((r) => (resolve = r)));
    render(<StockPage />);
    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    resolve([]); // settle so the pending fetch doesn't dangle past the test
    await screen.findByText(/No stock yet/);
  });

  // The error-branch render (getStock rejects → "Could not load stock. Is the
  // API up?") is not asserted here: in this Vitest 3.2.7 + React 19.1 stack, a
  // rejection the component *does* handle (its own `.catch` → setError) is still
  // flagged as unhandled through an internal promise the test can't reach — a
  // documented interaction (vitest-dev/vitest #7940, #5796). Every reviewer-
  // suggested workaround (pending-promise + reject-in-act; a scoped no-op
  // `.catch`) was tried and still tripped the detector. The error path itself is
  // a fixed message on any getStock rejection; the fetch client's error + refresh
  // transport is covered directly in api/client.test.ts (PR #111).

  it("shows the empty-state hint when there is no stock", async () => {
    mockGetStock.mockResolvedValue([]);
    render(<StockPage />);
    expect(await screen.findByText(/No stock yet/)).toBeInTheDocument();
  });

  it("renders each grade and sums available eggs across grades", async () => {
    mockGetStock.mockResolvedValue(ROWS);
    render(<StockPage />);

    expect(await screen.findByText("Grade A")).toBeInTheDocument();
    // Scope the restricted-count assertion to Grade B's row so it pins that
    // cell, not just "some 5 rendered somewhere".
    const gradeBRow = screen.getByRole("row", { name: /Grade B/ });
    expect(within(gradeBRow).getByText("5")).toBeInTheDocument();

    // 100 + 50 = 150 across 2 grades — the client-side reduce.
    expect(
      screen.getByText(
        (_, el) => el?.tagName === "P" && /^150 eggs available across 2 grade\(s\)\./.test(el.textContent ?? ""),
      ),
    ).toBeInTheDocument();
  });
});
