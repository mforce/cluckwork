// Apply the saved theme before first paint so there is no light/dark flash.
// Kept as a same-origin EXTERNAL file (not inline in index.html) so the
// Content-Security-Policy can stay `script-src 'self'` with no hash or nonce to
// maintain (#144). Loaded render-blocking in <head>, so it still runs before
// the first paint — same no-flash behaviour as the old inline script.
try {
  var t = localStorage.getItem("cluckwork.theme");
  if (t === "light" || t === "dark") document.documentElement.dataset.theme = t;
} catch (e) {}
