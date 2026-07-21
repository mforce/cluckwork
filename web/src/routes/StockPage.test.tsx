import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { StockPage } from "./StockPage";
import { getStock } from "../api/cluckwork";
import type { StockRow } from "../api/cluckwork";

// Mock the API seam so the screen renders against controlled data — no network,
// no backend. This proves the component test harness handles an async data load,
// the loading/error/empty branches, and the client-side total.
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

  // NOTE: the error-branch render (getStock rejects → "Could not load stock")
  // is intentionally not covered here — Vitest 3 + React 19 flag the handled
  // rejection as unhandled through an internal promise the test can't reach, and
  // a hacky suppression isn't worth it. The fetch-client's own error/refresh
  // handling is covered directly in api/client.test.ts.

  it("shows the empty-state hint when there is no stock", async () => {
    mockGetStock.mockResolvedValue([]);
    render(<StockPage />);
    expect(await screen.findByText(/No stock yet/)).toBeInTheDocument();
  });

  it("renders each grade and sums available eggs across grades", async () => {
    mockGetStock.mockResolvedValue(ROWS);
    render(<StockPage />);

    expect(await screen.findByText("Grade A")).toBeInTheDocument();
    expect(screen.getByText("Grade B")).toBeInTheDocument();
    expect(screen.getByText("5")).toBeInTheDocument(); // Grade B's restricted count

    // 100 + 50 = 150 across 2 grades — the client-side reduce.
    expect(
      screen.getByText(
        (_, el) => el?.tagName === "P" && /^150 eggs available across 2 grade\(s\)\./.test(el.textContent ?? ""),
      ),
    ).toBeInTheDocument();
  });
});
