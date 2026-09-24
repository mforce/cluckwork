import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
// #835 — the `opsz` entry, not the default one. The default entry's seven
// `url()`s are all `-wght-` faces, which pin every size to Inter's opsz 14 text
// cut, so `font-optical-sizing` on them does nothing (measured: a 32px string is
// 331px wide at opsz 14 and 310px at opsz 32; on the wght face both are 331px).
// Optical sizing is the CSS default, so loading this face IS the whole change:
// a 32px figure gets the display cut and a 14px row keeps the text cut.
import "@fontsource-variable/inter/opsz.css";
import { App } from "./App";
import "./styles.css";

const root = document.getElementById("root");
if (!root) throw new Error("Missing #root element");

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
