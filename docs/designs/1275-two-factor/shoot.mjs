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
const captureScreen = process.argv[3];
const capture = screen => !captureScreen || (captureScreen === 'passkeys' ? screen.startsWith('pk-') : screen === captureScreen);
const { chromium } = await import(pathToFileURL(join(playwright, 'index.mjs')).href);
const cache = join(homedir(), '.cache/ms-playwright');
const shell = readdirSync(cache).filter(name => name.startsWith('chromium_headless_shell-')).sort().at(-1);
const executablePath = process.env.CHROMIUM_PATH ?? (existsSync(chromium.executablePath()) ? chromium.executablePath() : join(cache, shell, 'chrome-headless-shell-linux64/chrome-headless-shell'));
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ executablePath });
const errors = [];
const results = [];
let captures = 0;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(pathToFileURL(join(here, 'two-factor-lab.html')).href);
  const fontData = readFileSync(font).toString('base64');
  await page.addStyleTag({ content: `@font-face{font-family:"Inter Variable";src:url(data:font/woff2;base64,${fontData});font-weight:100 900}.lab,.labnote{display:none!important}.stage{padding:0;justify-content:flex-start}` });
  await page.evaluate(() => document.fonts.ready);
  const screens = await page.evaluate(() => window.LAB.screens);
  assert(!captureScreen || captureScreen === 'passkeys' || Object.hasOwn(screens, captureScreen), `Unknown capture screen: ${captureScreen}`);
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
            if (width === 390 && (state.includes('daily') || state.endsWith('limited'))) {
              await page.locator('.dlg-content').evaluateAll(contents => contents.forEach(content => { content.scrollTop = content.scrollHeight; }));
              const visibility = await page.locator('#frame').evaluate(frame => {
                const content = frame.querySelector('.dlg-content');
                const captured = frame.getBoundingClientRect();
                const clipped = content?.getBoundingClientRect() ?? captured;
                const top = Math.max(captured.top, clipped.top);
                const bottom = Math.min(captured.bottom, clipped.bottom);
                const targets = {
                  code: frame.querySelector('input[autocomplete="one-time-code"]').closest('.field'),
                  notice: frame.querySelector('.alert.warning'),
                  switch: frame.querySelector('[data-action="use-recovery"], [data-action="use-authenticator"]'),
                };
                return Object.entries(targets).map(([name, element]) => {
                  const rect = element.getBoundingClientRect();
                  return {name, visible: rect.top >= top && rect.bottom <= bottom && rect.left >= captured.left && rect.right <= captured.right};
                });
              });
              for (const target of visibility) assert(target.visible, `${screen}/${direction}/${state}/${theme}: ${target.name} is outside the captured content viewport`);
            }
            if (width === 390 && await page.locator('.dlg').count()) {
              const geometry = await page.locator('.dlg').evaluate(dialog => {
                const content = dialog.querySelector('.dlg-content');
                const footer = dialog.querySelector('.dlg-actions');
                const action = footer.querySelector('button:last-child');
                const box = element => { const rect=element.getBoundingClientRect(); return {top:rect.top,bottom:rect.bottom,left:rect.left,right:rect.right}; };
                return {frame:box(document.querySelector('#frame')),dialog:box(dialog),content:box(content),footer:box(footer),action:box(action),inContent:content.contains(action),direction:getComputedStyle(footer).flexDirection};
              });
              assert.equal(geometry.inContent,false);
              assert.equal(geometry.direction,'row');
              assert(geometry.action.bottom <= geometry.frame.top + 844, `${screen}/${direction}/${state}: primary action falls below the phone viewport`);
              assert(geometry.action.bottom <= geometry.dialog.bottom && geometry.action.top >= geometry.footer.top, `${screen}/${direction}/${state}: primary action clipped vertically`);
              assert(geometry.action.left >= geometry.dialog.left && geometry.action.right <= geometry.dialog.right, `${screen}/${direction}/${state}: primary action clipped horizontally`);
              assert(geometry.content.bottom <= geometry.footer.top + 1, `${screen}/${direction}/${state}: content covers footer`);
            }
            if (screen.startsWith('pk-') && width === 390 && (state.endsWith('-code') || state.endsWith('-recovery'))) {
              await page.locator('.dlg-content').evaluateAll(contents => contents.forEach(content => { content.scrollTop = content.scrollHeight; }));
            }
            const name = `${screen}-${direction}-${width}-${state}-${theme}.png`;
            if (capture(screen)) {
              await page.locator('#frame').screenshot({ path: join(out, name), scale: 'css' });
              captures++;
            }
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
  for (const code of samples) assert.match(code, /^(?:[23456789BCDFGHJKMNPQRTVWXY]{4}-){3}[23456789BCDFGHJKMNPQRTVWXY]{4}$/);
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
  for (const config of [{ screen: 'stepup', state: 'dialog' }, { screen: 'stepup', state: 'consent' }, { screen: 'users', state: 'confirm' }]) {
    await page.evaluate(config => window.configure({ ...config, direction: 'a-password', theme: 'light' }), config);
    const password = page.getByLabel('Your current password', { exact: true });
    assert.equal(await password.getAttribute('autocomplete'), 'current-password');
    await page.getByLabel('Your authenticator code', { exact: true }).waitFor();
    await page.getByText(/Enter your password and a code from your authenticator app/).waitFor();
    await page.evaluate(config => window.configure({ ...config, direction: 'a' }), config);
    assert.equal(await page.getByLabel('Your current password', { exact: true }).count(), 0);
  }
  const proofCases = [
    {screen:'account',state:'regenerate',direction:'a'},
    {screen:'account',state:'turn-off',direction:'a'},
    {screen:'stepup',state:'dialog',direction:'a-password'},
    {screen:'stepup',state:'consent',direction:'a-password'},
    {screen:'users',state:'confirm',direction:'a-password'},
  ];
  const draft = samples[0].replaceAll('-', '');
  for (const config of proofCases) {
    await page.evaluate(config => window.configure({...config,width:390,theme:'light'}),config);
    const password=page.getByLabel('Your current password',{exact:true});
    await password.fill(draft);
    if (config.screen === 'stepup' && config.state === 'dialog') await page.getByLabel('New password',{exact:true}).fill(draft);
    if (config.state === 'consent') await page.getByRole('checkbox',{name:'Read farm data',exact:true}).uncheck();
    await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
    assert.equal(await page.getByLabel('Your current password',{exact:true}).inputValue(),draft);
    if (config.screen === 'stepup' && config.state === 'dialog') assert.equal(await page.getByLabel('New password',{exact:true}).inputValue(),draft);
    if (config.state === 'consent') assert.equal(await page.getByRole('checkbox',{name:'Read farm data',exact:true}).isChecked(),false);
    const recovery=page.getByLabel('Your recovery code',{exact:true});
    await recovery.fill(samples[0]);
    assert.equal((await recovery.inputValue()).length,19);
    await recovery.fill(samples[0].replaceAll('-',''));
    assert.equal((await recovery.inputValue()).length,16);
    assert.notEqual(await recovery.getAttribute('inputmode'),'numeric');
    await page.getByRole('button',{name:'Use an authenticator code',exact:true}).click();
    assert.equal(await page.getByLabel('Your current password',{exact:true}).inputValue(),draft);
    assert.equal(await page.getByLabel('Your authenticator code',{exact:true}).inputValue(),'');
  }
  const throttleCases = [
    {screen:'login',state:'daily',limited:'limited',direction:'a'},
    {screen:'login',state:'connect-daily',limited:'connect-limited',direction:'a'},
    {screen:'account',state:'regenerate-daily',limited:'regenerate-limited',direction:'a'},
    {screen:'account',state:'turn-off-daily',limited:'turn-off-limited',direction:'a'},
    {screen:'stepup',state:'daily',limited:'limited',direction:'a-password'},
    {screen:'stepup',state:'consent-daily',limited:'consent-limited',direction:'a-password'},
    {screen:'users',state:'daily',limited:'limited',direction:'a-password'},
  ];
  for (const config of throttleCases) {
    await page.evaluate(config => window.configure(config),config);
    assert.equal(await page.getByRole('button',{name:'Use a recovery code',exact:true}).isEnabled(),true);
    await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
    await page.getByLabel(config.screen === 'login' ? 'Recovery code' : 'Your recovery code',{exact:true}).fill(samples[0]);
    assert.equal(await page.locator('.btn.primary:disabled,.dlg-actions .btn.danger:disabled').count(),0);
    if(config.screen === 'login' && config.state.startsWith('connect')) assert.match(await page.locator('.next-mobile').innerText(),/Farm Ledger/);
    await page.getByRole('button',{name:'Use an authenticator code',exact:true}).click();
    assert.equal(await page.locator('.btn.primary:disabled,.dlg-actions .btn.danger:disabled').count(),1);
    assert.equal(await page.getByRole('button',{name:'Use a recovery code',exact:true}).isEnabled(),true);
    await page.evaluate(config => window.configure({...config,state:config.limited}),config);
    assert.equal(await page.getByRole('button',{name:'Use a recovery code',exact:true}).isDisabled(),true);
  }
  for (const width of [1280,390]) {
    const show=(screen,state,direction='a')=>page.evaluate(config=>window.configure(config),{screen,state,direction,width,theme:'light'});
    await show('pk-login','ready');
    assert.equal(await page.getByLabel('Farm code',{exact:true}).inputValue(),'');
    assert.equal(await page.getByLabel('Email',{exact:true}).getAttribute('autocomplete'),'username webauthn');
    await page.getByRole('button',{name:'Sign in with a passkey',exact:true}).click();
    await page.getByRole('heading',{name:'Signed in',exact:true}).waitFor();
    await show('pk-login','off');
    assert.equal(await page.getByRole('button',{name:/passkey/i}).count(),0);
    assert.equal(await page.getByLabel('Email',{exact:true}).getAttribute('autocomplete'),'username');
    await show('pk-login','connect');
    await page.getByRole('button',{name:'Sign in with a passkey',exact:true}).click();
    await page.getByRole('button',{name:'Review app permissions',exact:true}).click();
    await page.getByRole('heading',{name:'Let Farm Ledger act as you?',exact:true}).waitFor();
    await show('pk-login','second-passkey-only');
    assert.equal(await page.locator('input[type="password"],input[autocomplete="one-time-code"]').count(),0);
    assert.equal(await page.getByRole('button',{name:/authenticator|recovery/}).count(),0);
    await show('pk-account','both');
    await page.getByRole('button',{name:'Rename',exact:true}).first().click();
    assert.equal(await page.getByLabel('Passkey name',{exact:true}).evaluate(input=>input===document.activeElement),true);
    await page.getByLabel('Passkey name',{exact:true}).fill('Everyday phone');
    await page.getByRole('button',{name:'Save name',exact:true}).click();
    await page.getByRole('heading',{name:'Everyday phone',exact:true}).waitFor();
    await page.getByRole('button',{name:'Rename',exact:true}).first().click();
    await page.getByLabel('Passkey name',{exact:true}).fill('Phone');
    await page.getByRole('button',{name:'Save name',exact:true}).click();
    await show('pk-account','app-only');
    assert.equal(await page.getByRole('button',{name:'Turn off',exact:true}).count(),0);
    await page.getByRole('button',{name:'Add passkey',exact:true}).click();
    await page.getByLabel('Your current password',{exact:true}).waitFor();
    await page.getByLabel('Your authenticator code',{exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'Use a passkey',exact:true}).count(),0);
    await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
    assert.equal(await page.getByRole('button',{name:'Use a passkey',exact:true}).count(),0);
    await show('pk-account','limit');
    assert.equal(await page.getByRole('button',{name:'Add passkey',exact:true}).isDisabled(),true);
    assert.equal(await page.locator('.pk-key').count(),5);
    await show('pk-account','one-owner');
    assert.equal(await page.getByRole('button',{name:'Remove',exact:true}).count(),0);
    assert.match(await page.locator('.pk-locked').innerText(),/Cannot remove your last factor/);
    assert.equal(await page.locator('.pk-address').count(),0);
    await page.getByRole('button',{name:'Add passkey',exact:true}).click();
    assert.equal(await page.getByRole('button',{name:'Use password and authenticator code',exact:true}).count(),0);
    assert.equal(await page.getByLabel('Your current password',{exact:true}).count(),0);
    await show('pk-account','multi-address');
    assert.equal(await page.locator('.pk-address').count(),2);
    await show('pk-registration','stepup-password');
    await page.getByRole('button',{name:'Continue',exact:true}).click();
    assert.match(await page.getByRole('status').innerText(),/Other devices and connected apps/);
    await page.getByRole('button',{name:'Create passkey',exact:true}).click();
    await page.getByRole('heading',{name:'Passkey created',exact:true}).waitFor();
    for(const state of ['later','with-app']) {
      await show('pk-registration',state);
      assert.equal(await page.locator('.alert.warning').count(),0);
    }
    for(const [screen,state] of [['pk-stepup','dialog'],['pk-stepup','consent'],['pk-users','confirm'],['pk-account','remove']]) {
      await show(screen,state);
      const target=page.locator('#pk-target-password');
      if(await target.count())await target.fill(samples[0]);
      if(state==='consent')await page.getByRole('checkbox',{name:'Read farm data',exact:true}).uncheck();
      await page.getByRole('button',{name:'Use password and authenticator code',exact:true}).click();
      await page.getByLabel('Your current password',{exact:true}).fill(samples[0]);
      await page.getByLabel('Your authenticator code',{exact:true}).waitFor();
      await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
      assert.equal(await page.getByLabel('Your current password',{exact:true}).inputValue(),samples[0]);
      await page.getByLabel('Your recovery code',{exact:true}).waitFor();
      if(await target.count())assert.equal(await target.inputValue(),samples[0]);
      if(state==='consent')assert.equal(await page.getByRole('checkbox',{name:'Read farm data',exact:true}).isChecked(),false);
      await page.getByRole('button',{name:'Use a passkey',exact:true}).click();
      assert.equal(await page.getByLabel('Your current password',{exact:true}).count(),0);
      if(state==='consent')assert.equal(await page.getByRole('checkbox',{name:'Read farm data',exact:true}).isChecked(),false);
    }
    await show('pk-users','confirm');
    assert.match(await page.getByRole('dialog').innerText(),/Every session and connected app of this user ends/);
    await page.getByRole('button',{name:'Use passkey and remove',exact:true}).click();
    await page.getByText('Passkeys removed for ben@example.test. Every session and connected app of this user ended.',{exact:true}).waitFor();
    await show('pk-suggestion','offer');
    await page.getByRole('button',{name:'Not now',exact:true}).click();
    await page.getByRole('heading',{name:'Continue to your farm',exact:true}).waitFor();
    await show('pk-owner','setup');
    await page.getByText('Every Owner must set up a passkey or two-factor authentication',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up a passkey',exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up two-factor authentication',exact:true}).waitFor();
  }

  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'manifest.json'), JSON.stringify(results.filter(result => capture(result.screen)), null, 2) + '\n');
  console.log(`Checked ${results.length} frames; captured ${captures} at 1:1 into ${out}. No page errors or horizontal frame overflow. Interaction checks passed.`);
} finally {
  await browser.close();
}
