import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
// #835 — the `opsz` entry, not the default one, whose seven faces are all
// `-wght-` and render every size at Inter's opsz 14 text cut (measured: a 32px
// string is 331px wide at opsz 14 and 310px at 32; on the wght face, 331px at
// both). `font-optical-sizing: auto` is the CSS default, so this import is the
// whole change.
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
