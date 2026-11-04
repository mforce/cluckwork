import { readdirSync, readFileSync } from "node:fs";
import { dirname, extname, resolve, sep } from "node:path";
import { parse } from "@babel/parser";

// #1279 — the walk behind the step-up caller guard in UsersPage.test.tsx.
// It does not list the call shapes it accepts and trust the rest. It takes
// every name bound by an import from an API module (stepUp by any alias, and
// each namespace import) and requires each use of it to be a stepUp call.
// Anything else is a violation, so a new shape fails instead of escaping.
//
// The walk has no scope analysis: a local that shadows one of those names is
// reported too. That only ever fails closed.

const SRC = resolve(process.cwd(), "src");
const API_DIR = resolve(SRC, "api") + sep;
const API_MODULES = new Set([resolve(SRC, "api/client"), resolve(SRC, "api/session")]);
// The one module allowed to pass stepUp on: the API's public face.
const REEXPORTER = resolve(SRC, "api/client.ts");

const SOURCE_FILE = /\.[cm]?[jt]sx?$/;

// The module an import or export names, with any .ts/.js-style ending
// dropped, or null for a package import.
function moduleOf(file: string, specifier: unknown): string | null {
  if (typeof specifier !== "string" || !specifier.startsWith(".")) return null;
  return resolve(dirname(file), specifier).replace(SOURCE_FILE, "");
}
const isApiModule = (file: string, specifier: unknown) => API_MODULES.has(moduleOf(file, specifier) ?? "");

type Node = { type: string; loc?: { start: { line: number } } } & Record<string, unknown>;
const isNode = (value: unknown): value is Node =>
  typeof value === "object" && value !== null && typeof (value as Node).type === "string";
const nameOf = (node: unknown) => isNode(node) ? (node.name ?? node.value) : undefined;

const CALLS = new Set(["CallExpression", "OptionalCallExpression"]);
const MEMBERS = new Set(["MemberExpression", "OptionalMemberExpression"]);
// TypeScript nodes whose identifiers name a type, not a value. Any other TS
// parent, such as `import x = …` or `export = …`, holds a value reference.
const TYPE_POSITIONS = new Set([
  "TSTypeReference", "TSQualifiedName", "TSTypeQuery", "TSImportType", "TSTypePredicate",
  "TSPropertySignature", "TSMethodSignature", "TSInterfaceDeclaration", "TSTypeAliasDeclaration",
  "TSEnumDeclaration", "TSEnumMember", "TSModuleDeclaration", "TSTypeParameter",
  "TSExpressionWithTypeArguments", "TSInterfaceHeritage", "TSClassImplements",
]);
// TypeScript forms that bind or export a value through a name or a dotted
// entity; any tracked name inside one is a violation.
const VALUE_ALIASES = new Set(["TSImportEqualsDeclaration", "TSExportAssignment"]);

// An identifier in these positions names something else, not the import.
const isNotAReference = (parent: Node, key: string) =>
  (MEMBERS.has(parent.type) && key === "property" && !parent.computed)
  || (/^(Object|Class)(Property|Method)$|^ClassPrivateProperty$/.test(parent.type) && key === "key" && !parent.computed)
  || /^Import(Default|Namespace)?Specifier$/.test(parent.type)
  || (/^(LabeledStatement|BreakStatement|ContinueStatement)$/.test(parent.type) && key === "label")
  || TYPE_POSITIONS.has(parent.type);

function identifiersIn(node: unknown): string[] {
  if (!isNode(node)) return [];
  const own = node.type === "Identifier" ? [node.name as string] : [];
  const children = Object.entries(node).filter(([key]) => key !== "loc").flatMap(([, child]) => [child].flat());
  return [...own, ...children.flatMap(identifiersIn)];
}

export interface StepUpUses {
  // Each stepUp() call, named by its enclosing function declarations and
  // function-valued consts ("useFlockAccess.onAssign").
  sites: string[];
  // Every other use, as "line: what".
  violations: string[];
}

export function stepUpUses(source: string, file: string): StepUpUses {
  const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(/x$/.test(extname(file)) ? ["jsx" as const] : [])] });
  const body = tree.program.body as unknown as Node[];
  const direct = new Set<string>();
  const namespaces = new Set<string>();
  const sites: string[] = [];
  const violations: string[] = [];
  const at = (node: Node) => node.loc?.start.line ?? 0;

  for (const statement of body) {
    if (!isApiModule(file, (statement.source as Node | undefined)?.value)) continue;
    const specifiers = (statement.specifiers as Node[] | undefined) ?? [];
    if (statement.type === "ImportDeclaration") {
      for (const specifier of specifiers) {
        if (specifier.type === "ImportNamespaceSpecifier") namespaces.add(nameOf(specifier.local) as string);
        if (specifier.type === "ImportSpecifier" && nameOf(specifier.imported) === "stepUp") direct.add(nameOf(specifier.local) as string);
      }
    } else if (file !== REEXPORTER) {
      if (statement.type === "ExportAllDeclaration"
        || specifiers.some((s) => s.type === "ExportNamespaceSpecifier" || nameOf(s.local) === "stepUp"))
        violations.push(`${at(statement)}: re-export from an API module`);
    } else if (specifiers.some((s) => s.type === "ExportNamespaceSpecifier"
      || (nameOf(s.local) === "stepUp" && nameOf(s.exported) !== "stepUp"))
      || (statement.type === "ExportAllDeclaration" && statement.exported)) {
      // api/client.ts may pass stepUp on only under its own name, which the
      // imports above track.
      violations.push(`${at(statement)}: api/client.ts renames or nests stepUp`);
    }
  }

  const visit = (node: Node, parent: Node | null, key: string, grand: Node | null, names: string[]): void => {
    let inner = names;
    const id = node.id as Node | undefined;
    if (node.type === "FunctionDeclaration" && id?.name) inner = [...names, id.name as string];
    if (node.type === "VariableDeclarator" && id?.name && /Function/.test((node.init as Node | undefined)?.type ?? ""))
      inner = [...names, id.name as string];

    if (VALUE_ALIASES.has(node.type)) {
      if (identifiersIn(node).some((n) => direct.has(n) || namespaces.has(n)))
        violations.push(`${at(node)}: ${node.type} names an API import`);
      return;
    }

    const specifier = node.type === "ImportExpression" ? node.source as Node | undefined
      : node.type === "Import" && parent && CALLS.has(parent.type) ? (parent.arguments as Node[])[0] : null;
    if (specifier !== null && !file.startsWith(API_DIR)) {
      // A specifier the walk cannot read could name an API module, so it fails.
      const quasis = specifier?.type === "TemplateLiteral" && (specifier.expressions as Node[]).length === 0
        ? (specifier.quasis as Node[]) : null;
      const target = specifier?.type === "StringLiteral" ? specifier.value
        : quasis ? (quasis[0]?.value as { cooked?: string } | undefined)?.cooked : undefined;
      if (target === undefined) violations.push(`${at(node)}: dynamic import the walk cannot resolve`);
      else if (isApiModule(file, target)) violations.push(`${at(node)}: dynamic import of an API module`);
    }

    if (node.type === "Identifier" && parent && !isNotAReference(parent, key)) {
      if (direct.has(node.name as string)) {
        if (CALLS.has(parent.type) && key === "callee") sites.push(names.join("."));
        else violations.push(`${at(node)}: ${node.name} used other than as a call`);
      } else if (namespaces.has(node.name as string)) {
        const property = parent.property as Node | undefined;
        const member = MEMBERS.has(parent.type) && key === "object";
        const named = !member ? undefined
          : parent.computed ? (property?.type === "StringLiteral" ? property.value : null) : property?.name;
        if (!member) violations.push(`${at(node)}: namespace ${node.name} used as a value`);
        else if (named === null) violations.push(`${at(node)}: computed member of namespace ${node.name}`);
        else if (named === "stepUp") {
          if (grand && CALLS.has(grand.type) && grand.callee === parent) sites.push(names.join("."));
          else violations.push(`${at(node)}: ${node.name}.stepUp used other than as a call`);
        }
      }
    }

    for (const [childKey, child] of Object.entries(node)) {
      if (childKey === "loc") continue;
      if (Array.isArray(child)) { for (const item of child) if (isNode(item)) visit(item, node, childKey, parent, inner); }
      else if (isNode(child)) visit(child, node, childKey, parent, inner);
    }
  };
  for (const statement of body) visit(statement, null, "", null, []);
  return { sites, violations };
}

// A path under src/ the walk reads: any .ts/.js-style module outside tests.
export const isProductionSource = (path: string) =>
  SOURCE_FILE.test(path) && !path.includes(".test.") && !path.startsWith(`test${sep}`);

function productionSources(): string[] {
  return readdirSync(SRC, { recursive: true, encoding: "utf8" })
    .filter(isProductionSource)
    .sort()
    .map((path) => resolve(SRC, path));
}

// Every production stepUp() call site, and every other use as "file:line: what".
export function productionStepUpUses(): StepUpUses {
  const all: StepUpUses = { sites: [], violations: [] };
  for (const file of productionSources()) {
    const { sites, violations } = stepUpUses(readFileSync(file, "utf8"), file);
    all.sites.push(...sites);
    all.violations.push(...violations.map((v) => `${file.slice(SRC.length + 1)}:${v}`));
  }
  return all;
}

export const productionStepUpCallSites = () => productionStepUpUses().sites;
