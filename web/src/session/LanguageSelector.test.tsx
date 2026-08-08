import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { LanguageSelector } from "./LanguageSelector";
import { MeContext } from "./SessionContext";

vi.mock("../api/cluckwork", async (io) => ({ ...(await io<typeof import("../api/cluckwork")>()), putMeLanguage: vi.fn() }));
// Real production installs three packs today (en/es/tl, #182); mock down to
// one here so this file keeps covering the single-language hidden state. The
// real, multi-language rendering is covered by LanguageSelector.multilang.test.tsx.
vi.mock("../i18n", async (io) => {
  const actual = await io<typeof import("../i18n")>();
  return { ...actual, SUPPORTED_LANGUAGES: ["en"] };
});

function withMe(ui: React.ReactNode) {
  return render(
    <MeContext.Provider
      value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: null, preferredStepperUnit: null }}>
      {ui}
    </MeContext.Provider>,
  );
}

describe("LanguageSelector", () => {
  it("renders nothing while only one language is installed", () => {
    const { container } = withMe(<LanguageSelector />);
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByLabelText(/language/i)).toBeNull();
  });
});
