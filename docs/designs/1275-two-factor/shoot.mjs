import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const checkout = resolve(here, '../../..');
const installed = '/home/mforce/dev/cluckwork';
const playwright = process.env.PLAYWRIGHT_DIR ?? join(existsSync(join(checkout, 'tools/simulation/ui/node_modules/playwright')) ? checkout : installed, 'tools/simulation/ui/node_modules/playwright');
const font = process.env.INTER_WOFF2 ?? join(existsSync(join(checkout, 'web/node_modules/@fontsource-variable/inter')) ? checkout : installed, 'web/node_modules/@fontsource-variable/inter/files/inter-latin-opsz-normal.woff2');
const out = resolve(process.argv[2] ?? join(here, 'renders'));
const { chromium } = await import(pathToFileURL(join(playwright, 'index.mjs')).href);
const cache = join(homedir(), '.cache/ms-playwright');
const shell = readdirSync(cache).filter(name => name.startsWith('chromium_headless_shell-')).sort().at(-1);
const executablePath = process.env.CHROMIUM_PATH ?? (existsSync(chromium.executablePath()) ? chromium.executablePath() : join(cache, shell, 'chrome-headless-shell-linux64/chrome-headless-shell'));
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ executablePath });
const errors = [];
const results = [];
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(pathToFileURL(join(here, 'two-factor-lab.html')).href);
  const fontData = readFileSync(font).toString('base64');
  await page.addStyleTag({ content: `@font-face{font-family:"Inter Variable";src:url(data:font/woff2;base64,${fontData});font-weight:100 900}.lab,.labnote{display:none!important}.stage{padding:0;justify-content:flex-start}` });
  await page.evaluate(() => document.fonts.ready);
  const screens = await page.evaluate(() => window.LAB.screens);
  for (const [screen, spec] of Object.entries(screens)) {
    for (const direction of Object.keys(spec.directions)) {
      for (const width of [1280, 390]) {
        for (const theme of direction === spec.recommended ? ['light', 'dark'] : ['light']) {
          for (const state of spec.states) {
            await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
            await page.evaluate(config => window.configure(config), { screen, direction, width, theme, state });
            await page.evaluate(() => document.fonts.ready);
            const dimensions = await page.locator('#frame').evaluate(element => ({ width: element.clientWidth, scroll: element.scrollWidth, height: element.clientHeight }));
            assert.equal(dimensions.width, width);
            assert.equal(dimensions.scroll, width, `${screen}/${direction}/${state}/${width} overflows horizontally`);
            const name = `${screen}-${direction}-${width}-${state}-${theme}.png`;
            await page.locator('#frame').screenshot({ path: join(out, name), scale: 'css' });
            results.push({ screen, direction, width, state, theme, height: dimensions.height, file: name });
          }
        }
      }
    }
  }
  await page.evaluate(() => window.configure({ screen: 'recovery', state: 'new', direction: 'a', width: 390, theme: 'light' }));
  const samples = await page.locator('.codes li').allTextContents();
  assert.equal(samples.length, 10);
  assert.equal(new Set(samples).size, 10);
  for (const code of samples) assert.match(code, /^[23456789BCDFGHJKMNPQRTVWXY]{5}-[23456789BCDFGHJKMNPQRTVWXY]{5}$/);
  await page.getByRole('checkbox', { name: 'I saved my recovery codes somewhere safe.' }).check();
  await page.getByRole('button', { name: 'Done', exact: true }).click();
  await page.getByText('8', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Regenerate', exact: true }).click();
  assert.equal(await page.locator('[role="dialog"]').count(), 1);
  assert.equal(await page.locator('.app').evaluate(element => element.inert), true);
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('[role="dialog"]').count(), 0);
  await page.evaluate(() => window.configure({ screen: 'enrolment', state: 'scan', direction: 'b' }));
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByLabel('Authenticator code', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Confirm code', exact: true }).click();
  await page.getByRole('heading', { name: 'Save your recovery codes' }).waitFor();
  await page.evaluate(() => window.configure({ screen: 'login', direction: 'a', state: 'connect' }));
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  assert.match(await page.locator('.next-mobile').innerText(), /Farm Ledger/);
  assert.equal(await page.getByRole('alert').count() + await page.locator('[aria-invalid="true"]').count(), 1);
  await page.getByRole('button', { name: 'Use a recovery code', exact: true }).click();
  await page.getByLabel('Recovery code', { exact: true }).waitFor();
  assert.match(await page.locator('.next-mobile').innerText(), /Farm Ledger/);
  await page.evaluate(() => window.configure({ screen: 'users', state: 'selected' }));
  await page.getByRole('button', { name: 'Reset two-factor authentication', exact: true }).click();
  await page.getByLabel('Your authenticator code', { exact: true }).waitFor();
  await page.getByRole('dialog').getByRole('button', { name: 'Reset two-factor authentication', exact: true }).click();
  await page.getByText('Two-factor authentication was reset for ben@example.test.', { exact: true }).waitFor();
  await page.evaluate(() => window.configure({ screen: 'stepup', direction: 'a', state: 'consent' }));
  await page.getByRole('button', { name: 'Allow', exact: true }).click();
  await page.getByLabel('Your authenticator code', { exact: true }).waitFor();
  assert.match(await page.locator('.error-text').innerText(), /Nothing was connected/);
  await page.evaluate(() => window.configure({ screen: 'stepup', direction: 'b', state: 'consent' }));
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  assert.equal(await page.getByRole('dialog').count(), 1);
  await page.getByRole('dialog').getByRole('button', { name: 'Allow', exact: true }).click();
  assert.equal(await page.getByRole('dialog').locator('[aria-invalid="true"]').count(), 1);
  await page.keyboard.press('Escape');
  assert.equal(await page.getByRole('dialog').count(), 0);
  await page.getByRole('heading', { name: 'Let Farm Ledger act as you?' }).waitFor();
  for (const state of ['dialog', 'consent']) {
    await page.evaluate(state => window.configure({ screen: 'stepup', direction: 'a-password', state, theme: 'light' }), state);
    const password = page.getByLabel('Your current password', { exact: true });
    assert.equal(await password.getAttribute('autocomplete'), 'current-password');
    await page.getByLabel('Your authenticator code', { exact: true }).waitFor();
    await page.evaluate(state => window.configure({ direction: 'a', state }), state);
    assert.equal(await page.getByLabel('Your current password', { exact: true }).count(), 0);
  }
  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'manifest.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(`Captured ${results.length} frames at 1:1 into ${out}. No page errors or horizontal frame overflow. Interaction checks passed.`);
} finally {
  await browser.close();
}
