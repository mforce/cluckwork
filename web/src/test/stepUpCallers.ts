import { readdirSync, readFileSync } from "node:fs";
import { dirname, extname, resolve } from "node:path";
import { parse } from "@babel/parser";

// #1279 — the walk behind the step-up caller guard in UsersPage.test.tsx.
// It follows the import binding, not the text: every import of stepUp from
// the API modules, aliased or through a namespace, and every call to it.

const SRC = resolve(process.cwd(), "src");
const API_MODULES = new Set([resolve(SRC, "api/client"), resolve(SRC, "api/session")]);
// The one module allowed to pass stepUp on: the API's public face.
const REEXPORTER = resolve(SRC, "api/client.ts");

// The module an import or export names, with any .ts/.tsx spelling dropped,
// or null for a package import.
function moduleOf(file: string, specifier: string | undefined): string | null {
  if (!specifier?.startsWith(".")) return null;
  return resolve(dirname(file), specifier).replace(/\.tsx?$/, "");
}
const isApiModule = (file: string, specifier: string | undefined) => API_MODULES.has(moduleOf(file, specifier) ?? "");

type Node = {
  type?: string;
  id?: { name?: string };
  init?: { type?: string };
  source?: { value?: string };
  specifiers?: { type: string; local: { name?: string; value?: string }; imported?: { name?: string; value?: string } }[];
  callee?: Node;
  object?: Node;
  property?: Node;
  computed?: boolean;
  name?: string;
  value?: string;
};

// Each stepUp() call in one module, named by its enclosing function
// declarations and function-valued consts ("useFlockAccess.onAssign").
export function stepUpCallSites(source: string, file: string): string[] {
  const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(extname(file) === ".tsx" ? ["jsx" as const] : [])] });
  const direct = new Set<string>();
  const namespaces = new Set<string>();
  for (const statement of tree.program.body as Node[]) {
    if (statement.type !== "ImportDeclaration" || !isApiModule(file, statement.source?.value)) continue;
    for (const specifier of statement.specifiers ?? []) {
      if (specifier.type === "ImportNamespaceSpecifier") namespaces.add(specifier.local.name!);
      const imported = specifier.imported?.name ?? specifier.imported?.value;
      if (specifier.type === "ImportSpecifier" && imported === "stepUp") direct.add(specifier.local.name!);
    }
  }
  const isStepUp = (callee: Node | undefined) => {
    if (callee?.type === "Identifier") return direct.has(callee.name!);
    if (callee?.type !== "MemberExpression" && callee?.type !== "OptionalMemberExpression") return false;
    if (callee.object?.type !== "Identifier" || !namespaces.has(callee.object.name!)) return false;
    return callee.computed ? callee.property?.value === "stepUp" : callee.property?.name === "stepUp";
  };
  const sites: string[] = [];
  const visit = (node: unknown, names: string[]): void => {
    if (typeof node !== "object" || node === null) return;
    if (Array.isArray(node)) { for (const child of node) visit(child, names); return; }
    const n = node as Node;
    let inner = names;
    if (n.type === "FunctionDeclaration" && n.id?.name) inner = [...names, n.id.name];
    if (n.type === "VariableDeclarator" && n.id?.name && /Function/.test(n.init?.type ?? "")) inner = [...names, n.id.name];
    if ((n.type === "CallExpression" || n.type === "OptionalCallExpression") && isStepUp(n.callee)) sites.push(names.join("."));
    for (const [key, child] of Object.entries(node)) if (key !== "loc") visit(child, inner);
  };
  visit(tree.program, []);
  return sites;
}

// Each re-export that passes stepUp on (a named one, or `export *` from an API
// module), as "file: line". A caller importing through one would escape the
// walk above, so outside api/client.ts there must be none.
export function stepUpReExports(source: string, file: string): string[] {
  if (file === REEXPORTER) return [];
  const tree = parse(source, { sourceType: "module", plugins: ["typescript", ...(extname(file) === ".tsx" ? ["jsx" as const] : [])] });
  const found: string[] = [];
  for (const statement of tree.program.body as (Node & { loc?: { start: { line: number } } })[]) {
    if (!isApiModule(file, statement.source?.value)) continue;
    const named = statement.type === "ExportNamedDeclaration"
      && (statement.specifiers ?? []).some((specifier) => (specifier.local.name ?? specifier.local.value) === "stepUp");
    if (named || statement.type === "ExportAllDeclaration") found.push(`${file}:${statement.loc?.start.line}`);
  }
  return found;
}

function productionSources(): string[] {
  return readdirSync(SRC, { recursive: true, encoding: "utf8" })
    .filter((path) => [".ts", ".tsx"].includes(extname(path)) && !path.includes(".test.") && !path.startsWith("test/"))
    .sort()
    .map((path) => resolve(SRC, path));
}

export function productionStepUpReExports(): string[] {
  return productionSources().flatMap((file) => stepUpReExports(readFileSync(file, "utf8"), file));
}

// Every stepUp() call in the SPA's production modules.
export function productionStepUpCallSites(): string[] {
  return productionSources().flatMap((file) => stepUpCallSites(readFileSync(file, "utf8"), file));
}
