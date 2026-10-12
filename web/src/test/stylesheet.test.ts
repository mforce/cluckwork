import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { inlineImports, readStylesheet } from "./stylesheet";

function sheet(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), "stylesheet-"));
  for (const [rel, text] of Object.entries(files)) {
    mkdirSync(join(root, rel, ".."), { recursive: true });
    writeFileSync(join(root, rel), text);
  }
  return join(root, "index.css");
}

describe("inlineImports", () => {
  it("inlines nested imports relative to the importing file, in order", () => {
    const index = sheet({
      "index.css": '@import "./a/one.css";\n@import "./two.css";\n',
      "a/one.css": '.one {}\n@import "../b/nested.css";\n',
      "b/nested.css": ".nested {}\n",
      "two.css": ".two {}\n",
    });
    expect(inlineImports(index)).toBe(".one {}\n.nested {}\n.two {}\n");
  });

  it("throws on a missing file", () => {
    expect(() => inlineImports(sheet({ "index.css": '@import "./gone.css";\n' }))).toThrow(/ENOENT/);
  });

  it.each([
    ['@IMPORT "./a.css";\n'],
    ['@import url("./a.css");\n'],
    ['@import "./a.css" screen;\n'],
    ['@import "/a.css";\n'],
    ['@import "a.css";\n'],
  ])("throws on the unsupported form %j", (statement) => {
    expect(() => inlineImports(sheet({ "index.css": statement, "a.css": ".a {}\n" }))).toThrow(/unsupported @import/);
  });

  it("throws on a cycle", () => {
    const index = sheet({ "index.css": '@import "./a.css";\n', "a.css": '@import "./index.css";\n' });
    expect(() => inlineImports(index)).toThrow(/@import cycle/);
  });

  it("reads the app stylesheet with no @import left", () => {
    expect(readStylesheet()).not.toMatch(/@import/i);
  });
});
