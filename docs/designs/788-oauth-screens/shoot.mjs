// Renders every screen x direction x width of oauth-direction-lab.html at 1:1.
// Usage: node shoot.mjs [outDir]   (default: ./renders beside this file)
// Playwright and the Inter font come from the main checkout's installed
// node_modules; override with PLAYWRIGHT_DIR / INTER_WOFF2.
import { mkdirSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const PW = process.env.PLAYWRIGHT_DIR ?? '/home/mforce/dev/cluckwork/tools/simulation/ui/node_modules/playwright';
const INTER = process.env.INTER_WOFF2 ?? '/home/mforce/dev/cluckwork/web/node_modules/@fontsource-variable/inter/files/inter-latin-opsz-normal.woff2';
const OUT = process.argv[2] ?? join(HERE, 'renders');
const { chromium } = await import(pathToFileURL(join(PW, 'index.mjs')).href);
mkdirSync(OUT, { recursive: true });

const DIRS = ['a', 'b', 'c'];
const VPS = [1280, 390];
// full: the whole page, top to bottom. fixed: one 1280x800 / 390x844 viewport,
// scrolled to the part the screen adds (needed for dialogs and live errors).
const PLAN = {
  consent: { full: ['normal', 'details', 'wrong', 'widened', 'refused', 'long'], fixed: [] },
  login:   { full: ['normal', 'wrong', 'long'], fixed: [] },
  account: { full: ['normal', 'long'], fixed: ['normal', 'confirm', 'after', 'empty'] },
  owner:   { full: ['normal', 'long'], fixed: ['normal', 'confirm', 'after', 'empty'] },
  audit:   { full: ['normal', 'filtered', 'long'], fixed: [] },
};
const RECOMMENDED = { consent: 'e', login: 'b', account: 'a', owner: 'c', audit: 'c' };
// Consent A-C are kept as history; D-F are the compact rework. 'details' only exists in D-F.
const dirsFor = (screen) => screen === 'consent' ? ['a', 'b', 'c', 'd', 'e', 'f'] : DIRS;
const skip = (screen, dir, state) => screen === 'consent' && state === 'details' && 'abc'.includes(dir);

const SHOTS = [];
for (const [screen, { full, fixed }] of Object.entries(PLAN))
  for (const dir of dirsFor(screen)) for (const vp of VPS) {
    for (const state of full) if (!skip(screen, dir, state)) SHOTS.push([`${screen}-${dir}-${vp}-${state}`, { screen, dir, vp, state, full: true }]);
    for (const state of fixed) SHOTS.push([`${screen}-${dir}-${vp}-${state}-viewport`, { screen, dir, vp, state, full: false }]);
  }
for (const dir of dirsFor('consent')) for (const role of ['Admin', 'Manager', 'ReadOnly'])
  SHOTS.push([`consent-${dir}-1280-role-${role}`, { screen: 'consent', dir, vp: 1280, state: 'normal', role, full: true }]);
// The compact directions get the role variants at 390 too.
for (const dir of ['d', 'e', 'f']) for (const role of ['Admin', 'Manager', 'ReadOnly'])
  SHOTS.push([`consent-${dir}-390-role-${role}`, { screen: 'consent', dir, vp: 390, state: 'normal', role, full: true }]);
SHOTS.push(['consent-e-390-dark', { screen: 'consent', dir: 'e', vp: 390, state: 'normal', theme: 'dark', full: true }]);
for (const [screen, dir] of Object.entries(RECOMMENDED))
  SHOTS.push([`${screen}-${dir}-1280-dark`, { screen, dir, vp: 1280, state: 'normal', theme: 'dark', full: true }]);

// The installed Playwright may expect a browser build other than the one
// downloaded; fall back to whatever headless shell the cache holds.
const cache = join(homedir(), '.cache/ms-playwright');
const shell = readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell-')).sort().pop();
const browser = await chromium.launch().catch(() =>
  chromium.launch({ executablePath: process.env.CHROMIUM_PATH ?? join(cache, shell, 'chrome-headless-shell-linux64/chrome-headless-shell') }));
const errors = [], counts = [];
const page = await browser.newPage({ viewport: { width: 1320, height: 900 }, deviceScaleFactor: 1 });
page.on('pageerror', (e) => errors.push(e.message));
await page.goto(pathToFileURL(join(HERE, 'oauth-direction-lab.html')).href);
await page.addStyleTag({ content: `@font-face{font-family:"Inter Variable";src:url("${pathToFileURL(INTER).href}") format("woff2");font-weight:100 900}.lab,.labnote{display:none!important}.stage{padding:0!important}` });
await page.evaluate(() => document.fonts.ready);
for (const [name, cfg] of SHOTS) {
  await page.evaluate((c) => window.configure({ role: 'Worker', theme: 'light', ...c }), cfg);
  await page.waitForTimeout(60);
  await page.locator('#frame').screenshot({ path: join(OUT, name + '.png') });
  if (cfg.screen === 'consent' && cfg.state === 'normal' && !cfg.role && !cfg.theme)
    counts.push(`${name}: ${await page.evaluate(() => window.wordsBeforePassword())} words before the password field`);
}
console.log(counts.join('\n'));
await browser.close();
console.log(errors.length ? 'PAGE ERRORS:\n' + errors.join('\n') : `captured ${SHOTS.length} frames into ${OUT}, no page errors`);
