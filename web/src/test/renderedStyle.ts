import postcss from "postcss";

export function responsiveStyle(element: Element, media: string, property: string): string | undefined {
  const css = Array.from(document.querySelectorAll("style"), (style) => style.textContent ?? "").join("\n");
  let value: string | undefined;
  postcss.parse(css).walkAtRules("media", (query) => {
    if (query.params !== media) return;
    query.walkRules((rule) => {
      if (!element.matches(rule.selector)) return;
      rule.walkDecls(property, (declaration) => { value = declaration.value; });
    });
  });
  return value;
}
