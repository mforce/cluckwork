// jsdom 31 resolves var() in computed styles. Tests load no app stylesheet, so
// most tokens are undefined there and every `var(--token)` falls back to the
// inherited or initial value: two different tokens compute the same. These
// helpers give an undefined token a value no other token has, on :root, so a
// computed value still tells one token from another. setup.ts resets them
// after each test.
const LENGTH = /^(top|right|bottom|left|inset|width|height|gap)$|margin|padding/;
const defined = new Set<string>();

function define(name: string, length: boolean) {
  defined.add(name);
  const n = defined.size;
  document.documentElement.style.setProperty(name, length ? `${n}px` : `rgb(1, ${Math.floor(n / 256)}, ${n % 256})`);
}

const isUndefined = (name: string, scope: Element) => getComputedStyle(scope).getPropertyValue(name) === "";

// Every token the rendered document references gets a colour of its own.
export function defineReferencedVars(): void {
  const root = document.documentElement;
  for (const [, name] of root.outerHTML.matchAll(/var\((--[\w-]+)/g)) {
    if (isUndefined(name, root)) define(name, false);
  }
}

// What `property: var(name)` computes inside `scope`, for a test that names the
// token an element must use: `toHaveStyle({ color: cssVar("--error") })`.
export function cssVar(name: `--${string}`, property = "color", scope: Element = document.documentElement): string {
  if (isUndefined(name, scope)) define(name, LENGTH.test(property));
  const probe = document.createElement("span");
  probe.style.setProperty(property, `var(${name})`);
  scope.append(probe);
  const value = getComputedStyle(probe).getPropertyValue(property);
  probe.remove();
  return value;
}

export function resetCssVars(): void {
  for (const name of defined) document.documentElement.style.removeProperty(name);
  defined.clear();
}
