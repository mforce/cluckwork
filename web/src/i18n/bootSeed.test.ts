import { describe, it, expect, vi } from "vitest";

// REGRESSION GUARD for the "refresh reverts to English" report (#182 language
// hint). `pickInitialLanguage` is unit-tested in ../lib/languageHint.test.ts,
// but that only proves the PURE picker is correct — it does NOT prove that
// i18n/index.ts actually FEEDS the picker's result into i18next's initial `lng`.
// A refactor could sever that one wiring line and every other test would stay
// green (the authenticated bootstrap's changeLanguage would still mask it in
// most cases), while the pre-auth LOGIN screen — which has no /me to resolve
// from — silently fell back to English on every load. This asserts the wiring.
//
// We MOCK i18next here on purpose: the real singleton is an externalised dep
// that survives vi.resetModules(), so a genuine re-import of index.ts is only
// possible against a mock the module registry controls. With the mock we can
// reset the graph, re-run index.ts's module-load seeding against a chosen
// localStorage hint, and read exactly what `init` was seeded with. (This is the
// end-to-end cold-boot test the #253 PR flagged as the one residual gap.)
vi.mock("i18next", () => {
  const i18nMock = {
    use: vi.fn(() => i18nMock),
    init: vi.fn(() => Promise.resolve()),
    on: vi.fn(),
    // setup.ts's afterEach calls changeLanguage on the singleton; with i18next
    // mocked for this file it resolves to this no-op so teardown never throws.
    changeLanguage: vi.fn(() => Promise.resolve()),
  };
  return { default: i18nMock };
});
// index.ts only consumes `initReactI18next` from react-i18next; stub it so the
// mocked-i18next graph loads without pulling the real react bindings.
vi.mock("react-i18next", () => ({ initReactI18next: { type: "3rdParty" } }));

const KEY = "cluckwork.lang";

type I18nMock = {
  init: ReturnType<typeof vi.fn>;
  on: ReturnType<typeof vi.fn>;
};

// Re-run i18n/index.ts's module-load seeding with `hint` in storage and return
// i18next so its recorded init/on calls can be read. The mocked singleton
// PERSISTS across vi.resetModules() (the factory result is cached), so its call
// history is cleared here before re-importing index.ts — otherwise calls[0]
// would always be the very first boot's, not this one's.
async function bootWithHint(hint: string | null): Promise<I18nMock> {
  vi.resetModules();
  const i18n = (await import("i18next")).default as unknown as I18nMock;
  i18n.init.mockClear();
  i18n.on.mockClear();
  localStorage.clear();
  if (hint !== null) localStorage.setItem(KEY, hint);
  await import("./index"); // re-executes index.ts's module-load seeding
  return i18n;
}

describe("i18n boot seeds the initial language from the device hint", () => {
  it("initialises i18next in Spanish for an es hint (login screen, no /me)", async () => {
    const i18n = await bootWithHint("es");
    expect(i18n.init).toHaveBeenCalledTimes(1);
    expect(i18n.init.mock.calls[0][0]).toMatchObject({ lng: "es" });
  });

  it("initialises i18next in Tagalog for a tl hint", async () => {
    const i18n = await bootWithHint("tl");
    expect(i18n.init.mock.calls[0][0]).toMatchObject({ lng: "tl" });
  });

  it("falls back to English when no hint is stored", async () => {
    const i18n = await bootWithHint(null);
    expect(i18n.init.mock.calls[0][0]).toMatchObject({ lng: "en" });
  });

  it("falls back to English for a stale/removed/garbage hint", async () => {
    const i18n = await bootWithHint("fr");
    expect(i18n.init.mock.calls[0][0]).toMatchObject({ lng: "en" });
  });

  it("honours a case-mismatched hint (BCP-47 subtags are case-insensitive)", async () => {
    const i18n = await bootWithHint("ES");
    expect(i18n.init.mock.calls[0][0]).toMatchObject({ lng: "es" });
  });

  it("registers the languageChanged listener BEFORE init, so init's own first event sets <html lang>", async () => {
    const i18n = await bootWithHint("es");
    expect(i18n.on).toHaveBeenCalledWith("languageChanged", expect.any(Function));
    // invocationCallOrder is a global monotonic counter; within this single boot
    // the listener registration must precede the init() that emits the event.
    expect(i18n.on.mock.invocationCallOrder[0]).toBeLessThan(
      i18n.init.mock.invocationCallOrder[0],
    );
  });
});
