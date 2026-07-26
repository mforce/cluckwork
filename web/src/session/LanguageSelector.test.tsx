import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { LanguageSelector } from "./LanguageSelector";
import { MeContext } from "./SessionContext";

vi.mock("../api/cluckwork", async (io) => ({ ...(await io<typeof import("../api/cluckwork")>()), putMeLanguage: vi.fn() }));

function withMe(ui: React.ReactNode) {
  return render(
    <MeContext.Provider value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: null }}>
      {ui}
    </MeContext.Provider>,
  );
}

describe("LanguageSelector", () => {
  it("renders nothing while only English is installed", () => {
    // SUPPORTED_LANGUAGES === ["en"] today — no picker until a pack ships.
    const { container } = withMe(<LanguageSelector />);
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByLabelText(/language/i)).toBeNull();
  });
});
