import { chromium } from '/home/mforce/dev/cluckwork/tools/simulation/ui/node_modules/playwright/index.mjs';
const URL = 'file:///home/mforce/.cluckwork-slices/914-expand-lab/expand-chart-direction-lab.html';
const DIR = '/home/mforce/.cluckwork-slices/914-expand-lab/';
const DESK = { width: 1280, height: 800 }, PHONE = { width: 390, height: 844 };
const C = (o) => ({ concept: 'C', ...o });

const SHOTS = [];
for (const t of ['light', 'dark']) {
  // the collapsed card, as it will look with the Expand control
  SHOTS.push([`C2-1280-collapsed-${t}`, DESK,  C({ open: false, cardRange: 14, theme: t })]);
  SHOTS.push([`C2-390-collapsed-${t}`,  PHONE, C({ open: false, cardRange: 14, theme: t }), 'card']);
  // expanded, 90 days, window scrolled to the START of the range
  SHOTS.push([`C2-1280-90-start-${t}`,  DESK,  C({ open: true, range: 90, theme: t, scrollFrac: 0 })]);
  SHOTS.push([`C2-390-90-start-${t}`,   PHONE, C({ open: true, range: 90, theme: t, scrollFrac: 0 })]);
  // expanded, 90 days, scrolled mid-range — the box must have visibly moved
  SHOTS.push([`C2-1280-90-mid-${t}`,    DESK,  C({ open: true, range: 90, theme: t, scrollFrac: 0.45 })]);
  SHOTS.push([`C2-390-90-mid-${t}`,     PHONE, C({ open: true, range: 90, theme: t, scrollFrac: 0.45 })]);
  // a selected bar's readout (pointer selection, no focus ring)
  SHOTS.push([`C2-1280-90-readout-${t}`, DESK,  C({ open: true, range: 90, theme: t, selBack: 6 })]);
  SHOTS.push([`C2-390-90-readout-${t}`,  PHONE, C({ open: true, range: 90, theme: t, selBack: 4 })]);
  // keyboard focus on the strip
  SHOTS.push([`C2-1280-90-kbd-${t}`,    DESK,  C({ open: true, range: 90, theme: t, selBack: 6, kbd: true })]);
  SHOTS.push([`C2-390-90-kbd-${t}`,     PHONE, C({ open: true, range: 90, theme: t, selBack: 4, kbd: true })]);
  // 30 days at 1280: does the window still need to scroll?
  SHOTS.push([`C2-1280-30-${t}`,        DESK,  C({ open: true, range: 30, theme: t })]);
}
// one tl frame at 1280
SHOTS.push(['C2-1280-90-mid-tl', DESK, C({ open: true, range: 90, theme: 'light', locale: 'tl', scrollFrac: 0.45 })]);

const b = await chromium.launch();
const errs = [];
for (const [name, vp, cfg, scroll] of SHOTS) {
  const p = await b.newPage({ viewport: vp, deviceScaleFactor: 1 });
  p.on('pageerror', e => errs.push(name + ': ' + e.message));
  await p.goto(URL);
  await p.evaluate(c => window.configure(c), cfg);
  if (scroll === 'card') await p.evaluate(() => document.getElementById('laycard').scrollIntoView({ block: 'end' }));
  await p.waitForTimeout(400);
  // report the sync state the frame actually captured, so a wrong box is caught here
  const probe = cfg.open ? await p.evaluate(() => {
    const sc = document.getElementById('scroller'), box = document.getElementById('winbox');
    return sc && box ? { left: box.style.left, width: box.style.width,
      sl: Math.round(sc.scrollLeft), max: Math.round(sc.scrollWidth - sc.clientWidth) } : null;
  }) : null;
  await p.screenshot({ path: DIR + name + '.png' });
  await p.close();
  console.log(name, probe ? JSON.stringify(probe) : '');
}
await b.close();
console.log(errs.length ? 'ERRORS:\n' + errs.join('\n') : 'captured ' + SHOTS.length + ' frames, no page errors');
