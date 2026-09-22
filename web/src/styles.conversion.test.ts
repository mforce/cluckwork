import { readdirSync, readFileSync } from "node:fs";
import { extname, relative, resolve } from "node:path";
import { parse } from "@babel/parser";
import postcss from "postcss";
import selectorParser from "postcss-selector-parser";
import { describe, expect, it } from "vitest";
import { DAY_SLOT_KINDS, GRADE_COLOURS } from "./lib/dashboard";
import { GRADING_TONES } from "./lib/grading";
import { STATUS_BADGE_CLASSES } from "./components/StatusBadge";
import { DASHBOARD_DELTA_CLASSES } from "./routes/Dashboard";

type AstNode = { type: string; start?: number | null; end?: number | null; [key: string]: unknown };

const sourceRoot = resolve(process.cwd(), "src");
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null;
const isNode = (value: unknown): value is AstNode =>
  isRecord(value) && "type" in value && typeof value.type === "string";

function walk(value: unknown, visit: (node: AstNode) => void): void {
  if (!isNode(value)) return;
  const node = value;
  visit(node);
  for (const value of Object.values(node)) {
    if (isNode(value)) walk(value, visit);
    else if (Array.isArray(value)) for (const child of value) if (isNode(child)) walk(child, visit);
  }
}

function productionFiles(extensions: readonly string[]): string[] {
  const visit = (directory: string): string[] => readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) return entry.name === "test" ? [] : visit(path);
    return extensions.includes(extname(path)) && !entry.name.includes(".test.") ? [path] : [];
  });
  return visit(sourceRoot).sort();
}

function declaredClasses(): Set<string> {
  const found = new Set<string>();
  const css = postcss.parse(readFileSync(resolve(sourceRoot, "styles.css"), "utf8"));
  css.walkRules((rule) => {
    selectorParser((selectors) => selectors.walkClasses((node) => { found.add(node.value); })).processSync(rule.selector);
  });
  return found;
}

const exactDynamicValues = new Map<string, readonly string[]>([
  ["components/DayStrip.tsx:s.kind", DAY_SLOT_KINDS],
  ["components/GradingChip.tsx:tone", GRADING_TONES],
  ["components/StatusBadge.tsx:variant", STATUS_BADGE_CLASSES],
  ["components/StockBar.tsx:s.colorIndex", Array.from({ length: GRADE_COLOURS }, (_, index) => String(index + 1))],
  ["components/ThemeToggle.tsx:className", [""]],
  ["routes/Dashboard.tsx:deltaClass(trendData.henDay.delta)", DASHBOARD_DELTA_CLASSES],
  ["routes/Dashboard.tsx:s.colorIndex", Array.from({ length: GRADE_COLOURS }, (_, index) => String(index + 1))],
]);
const consultedDynamicValues = new Set<string>();

const nonStyleClassHooks = new Set([
  "brand-mark", "brand-splash-continue", "busy-label", "choice-set", "dash-sales-list", "day-none",
  "day-recorded", "day-unrecorded", "dialog-backdrop", "entry-actions", "field", "hint", "more-group",
  "named-picker-retry", "named-picker-trigger", "numfield", "numfield-step-unit", "spinner", "update-banner",
]);

function field(node: AstNode, name: string): AstNode {
  const value = node[name];
  if (!isNode(value)) throw new Error(`${node.type}.${name} is not an expression`);
  return value;
}

function text(node: AstNode, name: string): string {
  const value = node[name];
  if (typeof value !== "string") throw new Error(`${node.type}.${name} is not text`);
  return value;
}

function cartesian(parts: readonly (readonly string[])[]): string[] {
  return parts.reduce<string[]>((prefixes, values) =>
    prefixes.flatMap((prefix) => values.map((value) => prefix + value)), [""]);
}

function combinations(parts: readonly (readonly string[])[]): string[][] {
  return parts.reduce<string[][]>((rows, values) =>
    rows.flatMap((row) => values.map((value) => [...row, value])), [[]]);
}

function sourceText(node: AstNode, source: string): string {
  if (typeof node.start !== "number" || typeof node.end !== "number") throw new Error("expression has no source range");
  return source.slice(node.start, node.end);
}

function valuesOf(node: AstNode, file: string, source: string): string[] {
  if (node.type === "StringLiteral") return [text(node, "value")];
  if (node.type === "Identifier" && text(node, "name") === "undefined") return [""];
  if (node.type === "ConditionalExpression") {
    return [...valuesOf(field(node, "consequent"), file, source), ...valuesOf(field(node, "alternate"), file, source)];
  }
  if (node.type === "LogicalExpression") {
    if (node.operator === "&&") return ["", ...valuesOf(field(node, "right"), file, source)];
    return [...valuesOf(field(node, "left"), file, source), ...valuesOf(field(node, "right"), file, source)];
  }
  if (node.type === "TemplateLiteral") {
    const quasis = node.quasis;
    const expressions = node.expressions;
    if (!Array.isArray(quasis) || !Array.isArray(expressions)) throw new Error("invalid template literal");
    const parts: string[][] = [];
    for (let index = 0; index < quasis.length; index += 1) {
      const quasi = quasis[index];
      if (!isNode(quasi) || !isRecord(quasi.value) || typeof quasi.value.cooked !== "string") throw new Error("invalid template element");
      parts.push([quasi.value.cooked]);
      const expression = expressions[index];
      if (expression !== undefined) {
        if (!isNode(expression)) throw new Error("invalid template expression");
        parts.push(valuesOf(expression, file, source));
      }
    }
    return cartesian(parts);
  }
  if (node.type === "CallExpression" && sourceText(field(node, "callee"), source).endsWith(".join")) {
    let receiver = field(field(node, "callee"), "object");
    if (receiver.type === "CallExpression" && sourceText(field(receiver, "callee"), source).endsWith(".filter")) {
      receiver = field(field(receiver, "callee"), "object");
    }
    const args = node.arguments;
    if (receiver.type === "ArrayExpression" && Array.isArray(args) && isNode(args[0]) && args[0].type === "StringLiteral") {
      const elements = receiver.elements;
      if (!Array.isArray(elements) || !elements.every(isNode)) throw new Error("unsupported array class expression");
      const separator = text(args[0], "value");
      return combinations(elements.map((element) => valuesOf(element, file, source)))
        .map((values) => values.filter(Boolean).join(separator));
    }
  }

  const expression = sourceText(node, source);
  const sourceKey = `${relative(sourceRoot, file)}:${expression}`;
  const exact = exactDynamicValues.get(sourceKey);
  if (exact !== undefined) {
    consultedDynamicValues.add(sourceKey);
    return [...exact];
  }
  throw new Error(`${sourceKey}: unsupported dynamic className expression`);
}

function producedClasses(): Set<string> {
  const found = new Set<string>();
  consultedDynamicValues.clear();
  for (const file of productionFiles([".tsx"])) {
    const source = readFileSync(file, "utf8");
    const tree = parse(source, { sourceType: "module", plugins: ["typescript", "jsx"] });
    walk(tree.program, (node) => {
      const jsxClassName = node.type === "JSXAttribute" && isNode(node.name) && node.name.name === "className"
        && isNode(node.value) ? node.value : null;
      const objectClassName = propertyName(node) === "className" && isNode(node.value)
        && node.value.type !== "AssignmentPattern" ? node.value : null;
      const valueNode = jsxClassName ?? objectClassName;
      if (valueNode === null) return;
      const values = valueNode.type === "StringLiteral"
        ? [text(valueNode, "value")]
        : valueNode.type === "JSXExpressionContainer" && isNode(valueNode.expression)
          ? valuesOf(valueNode.expression, file, source)
          : valuesOf(valueNode, file, source);
      for (const value of values) for (const token of value.split(/\s+/).filter(Boolean)) found.add(token);
    });
  }
  return found;
}

describe("CSS conversion class manifest (#824 G1)", () => {
  it("declares every class produced by routes and components", () => {
    const declared = declaredClasses();
    const produced = producedClasses();
    const missing = [...produced].filter((token) => !declared.has(token) && !nonStyleClassHooks.has(token)).sort();
    expect(missing).toEqual([]);
    expect([...nonStyleClassHooks].filter((token) => !produced.has(token))).toEqual([]);
    expect([...nonStyleClassHooks].filter((token) => declared.has(token))).toEqual([]);
    expect([...exactDynamicValues.keys()].filter((key) => !consultedDynamicValues.has(key))).toEqual([]);
  });
});

function propertyName(node: AstNode): string | null {
  if (node.type !== "ObjectProperty" || !isNode(node.key)) return null;
  if (node.key.type === "Identifier") return text(node.key, "name");
  if (node.key.type === "StringLiteral") {
    const name = text(node.key, "value");
    return name === "text-transform" ? "textTransform" : name;
  }
  return null;
}

function uppercaseKind(node: AstNode, source: string): boolean {
  if (node.type === "StringLiteral") return text(node, "value").toLowerCase() === "uppercase";
  if (node.type === "TemplateLiteral" && Array.isArray(node.expressions) && node.expressions.length === 0) {
    return sourceText(node, source).slice(1, -1).toLowerCase() === "uppercase";
  }
  if (node.type === "ConditionalExpression") {
    return uppercaseKind(field(node, "consequent"), source) || uppercaseKind(field(node, "alternate"), source);
  }
  throw new Error(`unsupported textTransform expression: ${sourceText(node, source)}`);
}

function elevationValue(node: AstNode, source: string): string {
  if (node.type === "NumericLiteral") return String(node.value);
  if (node.type === "StringLiteral") return text(node, "value");
  throw new Error(`unsupported elevation expression: ${sourceText(node, source)}`);
}

function shadowKind(node: AstNode, source: string): "none" | "inset" | "drop" {
  if (node.type === "StringLiteral") {
    const value = text(node, "value").trim().toLowerCase();
    return value === "none" ? "none" : value.startsWith("inset") ? "inset" : "drop";
  }
  if (node.type === "TemplateLiteral") {
    const raw = sourceText(node, source).slice(1, -1).trim().toLowerCase();
    return raw.startsWith("inset") ? "inset" : "drop";
  }
  if (node.type === "ConditionalExpression") {
    const kinds = [shadowKind(field(node, "consequent"), source), shadowKind(field(node, "alternate"), source)];
    return kinds.includes("drop") ? "drop" : kinds.includes("inset") ? "inset" : "none";
  }
  if (node.type === "ArrowFunctionExpression") return shadowKind(field(node, "body"), source);
  if (sourceText(node, source) === "base.shadows[8]") return "drop";
  throw new Error(`unsupported boxShadow expression: ${sourceText(node, source)}`);
}

describe("MUI source policy (#824)", () => {
  it("keeps non-inset shadows and uppercase overrides at the reviewed sites", () => {
    const dropShadows: string[] = [];
    const uppercase: string[] = [];
    for (const file of productionFiles([".ts", ".tsx"])) {
      const source = readFileSync(file, "utf8");
      const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(extname(file) === ".tsx" ? ["jsx" as const] : [])] });
      walk(tree.program, (node) => {
        const name = propertyName(node);
        if (name === null || !isNode(node.value)) return;
        const identity = `${relative(sourceRoot, file)}:${sourceText(node.value, source)}`;
        if (name === "boxShadow" && shadowKind(node.value, source) === "drop") dropShadows.push(identity);
        if (name === "textTransform" && uppercaseKind(node.value, source)) uppercase.push(identity);
      });
    }
    expect(dropShadows.sort()).toEqual([
      "pwa/UpdatePrompt.tsx:\"var(--shadow-bar)\"",
      "theme/FarmThemeProvider.tsx:base.shadows[8]",
      "theme/FarmThemeProvider.tsx:base.shadows[8]",
    ]);
    expect(uppercase.sort()).toEqual([
      "components/FieldConsole.tsx:\"uppercase\"",
      "routes/Dashboard.tsx:\"uppercase\"",
      "routes/SalesPage.tsx:\"uppercase\"",
    ]);
  });

  it("allows nonzero elevation only on the daily-entry action bar", () => {
    const nonzero: string[] = [];
    for (const file of productionFiles([".ts", ".tsx"])) {
      const source = readFileSync(file, "utf8");
      const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(extname(file) === ".tsx" ? ["jsx" as const] : [])] });
      walk(tree.program, (node) => {
        const jsxElevation = node.type === "JSXAttribute" && isNode(node.name) && node.name.name === "elevation"
          && isNode(node.value) ? node.value : null;
        const objectElevation = propertyName(node) === "elevation" && isNode(node.value) ? node.value : null;
        const valueNode = jsxElevation?.type === "JSXExpressionContainer" && isNode(jsxElevation.expression)
          ? jsxElevation.expression : jsxElevation ?? objectElevation;
        if (valueNode === null) return;
        const value = elevationValue(valueNode, source);
        if (value !== "0") nonzero.push(`${relative(sourceRoot, file)}:${value}`);
      });
    }
    expect(nonzero).toEqual(["routes/DailyEntryPage.tsx:4"]);
  });
});
