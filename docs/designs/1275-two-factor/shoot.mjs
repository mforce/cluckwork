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
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.getByLabel('Authenticator code',{exact:true}).waitFor();
    assert.doesNotMatch(await page.locator('#frame').innerText(),/passkey/i);
    await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
    await page.getByLabel('Recovery code',{exact:true}).waitFor();
    assert.doesNotMatch(await page.locator('#frame').innerText(),/passkey/i);
    await page.getByRole('button',{name:'Back to sign in',exact:true}).click();
    assert.equal(await page.getByLabel('Email',{exact:true}).getAttribute('autocomplete'),'username');
    for(const recovery of [false,true]) {
      await show('pk-login','off');
      await page.getByRole('button',{name:'Sign in',exact:true}).click();
      if(recovery)await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
      await page.getByRole('button',{name:'Sign in',exact:true}).click();
      await page.getByRole('button',{name:'Continue',exact:true}).click();
      await page.getByRole('heading',{name:'Account',exact:true}).waitFor();
      assert.doesNotMatch(await page.locator('#frame').innerText(),/passkey/i);
      assert.equal(await page.locator('.pk-key').count(),0);
      assert.equal(await page.getByRole('heading',{name:'Recovery codes',exact:true}).count(),1);
    }
    await show('pk-login','connect');
    await page.getByRole('button',{name:'Sign in with a passkey',exact:true}).click();
    await page.getByRole('button',{name:'Review app permissions',exact:true}).click();
    await page.getByRole('heading',{name:'Let Farm Ledger act as you?',exact:true}).waitFor();
    await show('pk-login','connect');
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    for(const method of ['Use an authenticator code','Use a recovery code','Use a passkey']) {
      assert.match(await page.locator(width===390?'.next-mobile':'.next').innerText(),/Farm Ledger/);
      await page.getByRole('button',{name:method,exact:true}).click();
    }
    assert.match(await page.locator(width===390?'.next-mobile':'.next').innerText(),/Farm Ledger/);
    await page.getByRole('button',{name:'Use a passkey',exact:true}).click();
    await page.getByRole('heading',{name:'Continue to Farm Ledger',exact:true}).waitFor();
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
    await page.getByRole('button',{name:'Continue',exact:true}).click();
    assert.equal(await page.locator('.alert.warning').count(),0);
    await page.getByLabel('Passkey name',{exact:true}).fill('New phone');
    await page.getByRole('button',{name:'Create passkey',exact:true}).click();
    await page.getByText('New phone is ready to use.',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Done',exact:true}).click();
    assert.equal(await page.locator('.pk-key').count(),1);
    await page.getByRole('heading',{name:'New phone',exact:true}).waitFor();
    await page.getByRole('heading',{name:'Recovery codes',exact:true}).waitFor();
    assert.equal(await page.getByText('On',{exact:true}).count(),1);
    assert.equal(await page.getByRole('button',{name:'Remove',exact:true}).count(),1);
    await show('pk-account','limit');
    assert.equal(await page.getByRole('button',{name:'Add passkey',exact:true}).isDisabled(),true);
    assert.equal(await page.locator('.pk-key').count(),5);
    await show('pk-account','one-owner');
    assert.equal(await page.getByRole('button',{name:'Remove',exact:true}).count(),0);
    assert.match(await page.locator('.pk-locked').innerText(),/Cannot remove your last factor/);
    assert.equal(await page.locator('.pk-address').count(),0);
    await page.getByRole('button',{name:'Rename',exact:true}).click();
    await page.getByLabel('Passkey name',{exact:true}).fill('Owner phone');
    await page.getByRole('button',{name:'Save name',exact:true}).click();
    await page.getByRole('heading',{name:'Owner phone',exact:true}).waitFor();
    assert.equal(await page.locator('.pk-key').count(),1);
    assert.equal(await page.getByText('Off',{exact:true}).count(),1);
    assert.equal(await page.getByRole('heading',{name:'Recovery codes',exact:true}).count(),0);
    assert.equal(await page.getByRole('button',{name:'Remove',exact:true}).count(),0);
    assert.match(await page.locator('.pk-locked').innerText(),/Cannot remove your last factor/);
    await page.getByRole('button',{name:'Rename',exact:true}).click();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('heading',{name:'Owner phone',exact:true}).waitFor();
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
      assert.equal(await page.getByLabel('Your current password',{exact:true}).evaluate(input=>input===document.activeElement),true);
      await page.getByLabel('Your current password',{exact:true}).fill(samples[0]);
      await page.getByLabel('Your authenticator code',{exact:true}).waitFor();
      await page.getByRole('button',{name:'Use a recovery code',exact:true}).click();
      assert.equal(await page.getByLabel('Your current password',{exact:true}).inputValue(),samples[0]);
      assert.equal(await page.getByLabel('Your current password',{exact:true}).evaluate(input=>input===document.activeElement),true);
      await page.getByLabel('Your recovery code',{exact:true}).waitFor();
      if(await target.count())assert.equal(await target.inputValue(),samples[0]);
      if(state==='consent')assert.equal(await page.getByRole('checkbox',{name:'Read farm data',exact:true}).isChecked(),false);
      await page.getByRole('button',{name:'Use a passkey',exact:true}).click();
      assert.equal(await page.getByLabel('Your current password',{exact:true}).count(),0);
      if(state==='consent')assert.equal(await page.getByRole('checkbox',{name:'Read farm data',exact:true}).isChecked(),false);
      else {
        assert.equal(await page.getByRole('dialog').evaluate(dialog=>dialog.contains(document.activeElement)),true);
        assert.match(await page.evaluate(()=>document.activeElement.textContent),/Use passkey/);
        await page.keyboard.press('Escape');
        assert.equal(await page.getByRole('dialog').count(),0);
      }
    }
    await show('pk-stepup','dialog');
    await page.getByRole('button',{name:'Use password and authenticator code',exact:true}).click();
    await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('dialog').count(),0);
    await show('pk-users','confirm');
    assert.match(await page.getByRole('dialog').innerText(),/Every session and connected app of this user ends/);
    await page.getByRole('button',{name:'Use passkey and remove',exact:true}).click();
    await page.getByText('Passkeys removed for ben@example.test. Every session and connected app of this user ended.',{exact:true}).waitFor();
    for(const state of ['offer','offer-app']) {
      await show('pk-suggestion',state);
      await page.getByRole('button',{name:'Not now',exact:true}).click();
      await page.getByRole('button',{name:'Continue',exact:true}).click();
      await page.getByRole('heading',{name:'Account',exact:true}).waitFor();
      assert.equal(await page.locator('.pk-key').count(),0);
      assert.equal(await page.getByText(state==='offer-app'?'On':'Off',{exact:true}).count(),1);
      assert.equal(await page.getByRole('heading',{name:'Recovery codes',exact:true}).count(),state==='offer-app'?1:0);
      await show('pk-suggestion',state);
      await page.getByRole('button',{name:'Create passkey',exact:true}).click();
      await page.getByLabel('Your current password',{exact:true}).waitFor();
      assert.equal(await page.getByLabel('Your authenticator code',{exact:true}).count(),state==='offer-app'?1:0);
      assert.equal(await page.getByRole('button',{name:'Use a passkey',exact:true}).count(),0);
      await page.getByRole('button',{name:'Cancel',exact:true}).click();
      await page.getByRole('heading',{name:'Sign in faster with a passkey',exact:true}).waitFor();
    }
    await show('pk-owner','setup');
    await page.getByText('Every Owner must set up a passkey or two-factor authentication',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up a passkey',exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up two-factor authentication',exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up a passkey',exact:true}).click();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('heading',{name:'Protect your account',exact:true}).waitFor();
    assert.equal(await page.locator('.pk-key').count(),0);
    assert.equal(await page.getByRole('heading',{name:'Recovery codes',exact:true}).count(),0);
    await page.getByRole('button',{name:'Set up a passkey',exact:true}).click();
    assert.equal(await page.getByLabel('Your authenticator code',{exact:true}).count(),0);
    await page.getByRole('button',{name:'Continue',exact:true}).click();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await page.getByRole('heading',{name:'Protect your account',exact:true}).waitFor();
    await page.getByRole('button',{name:'Set up a passkey',exact:true}).click();
    await page.getByRole('button',{name:'Continue',exact:true}).click();
    await page.getByRole('button',{name:'Create passkey',exact:true}).click();
    await page.getByRole('button',{name:'Done',exact:true}).click();
    await page.getByRole('button',{name:'Rename',exact:true}).click();
    await page.getByRole('button',{name:'Save name',exact:true}).click();
    await page.getByRole('heading',{name:'Account',exact:true}).waitFor();
    assert.equal(await page.locator('.pk-key').count(),1);
    assert.equal(await page.getByText('Off',{exact:true}).count(),1);
    assert.equal(await page.getByRole('button',{name:'Remove',exact:true}).count(),0);
  }

  const exits=[];
  const registrationFacts=state=>({
    app:!['stepup-password','stepup-passkey-only','first','waiting','success','cancelled','unsupported'].includes(state),
    keys:['stepup-passkey','stepup-passkey-only','stepup-code','stepup-recovery','later','success'].includes(state)?1:0,
  });
  const accountIs=async({app,keys,enabled=true})=>{
    await page.getByRole('heading',{name:'Account',exact:true}).waitFor();
    assert.equal(await page.locator('.pk-key').count(),keys);
    assert.equal(await page.getByText(app?'On':'Off',{exact:true}).count(),1);
    assert.equal(await page.getByRole('heading',{name:'Recovery codes',exact:true}).count(),app?1:0);
    assert.equal(await page.getByRole('button',{name:'Add passkey',exact:true}).count(),enabled?1:0);
    if(app&&keys===0) {
      assert.equal(await page.getByText('Your passkeys will still be available after you turn off two-factor authentication.',{exact:true}).count(),0);
      if(await page.getByRole('button',{name:'Turn off',exact:true}).count())await page.getByText('Remove the extra code at sign-in.',{exact:true}).waitFor();
    }
  };
  for(const width of [1280,390]) {
    await page.setViewportSize({width,height:width===390?844:800});
    const show=(screen,state,direction='a')=>page.evaluate(config=>window.configure(config),{screen,state,direction,width,theme:'light'});
    for(const [screen,spec] of Object.entries(screens).filter(([screen])=>screen.startsWith('pk-'))) {
      for(const direction of Object.keys(spec.directions)) for(const state of spec.states) {
        await show(screen,state,direction);
        const names=await page.getByRole('button',{name:/^(Continue|Done|Cancel)$/}).allTextContents();
        for(const name of names) {
          await show(screen,state,direction);
          const exit=page.getByRole('button',{name,exact:true});
          if(await exit.isDisabled())await page.getByRole('button',{name:'Use a passkey',exact:true}).click();
          await exit.click();
          if(screen==='pk-registration') {
            const facts=registrationFacts(state);
            if(name==='Continue') {
              await page.getByRole('button',{name:'Create passkey',exact:true}).click();
              await page.getByRole('button',{name:'Done',exact:true}).click();
              facts.keys++;
            }
            await accountIs(facts);
          } else if(screen==='pk-account')await accountIs({app:true,keys:2});
          else if(screen==='pk-login')await accountIs({app:true,keys:state==='off-signed-in'?0:2,enabled:state!=='off-signed-in'});
          else if(screen==='pk-suggestion')await accountIs({app:state==='skipped-app',keys:0});
          else if(screen==='pk-stepup'&&state.startsWith('consent'))await page.getByRole('heading',{name:'Connection cancelled',exact:true}).waitFor();
          else {
            await page.getByRole('heading',{name:'Users',exact:true}).waitFor();
            assert.equal(await page.getByRole('dialog').count(),0);
            assert.match(await page.locator('.inspector').innerText(),/2 registered/);
          }
          exits.push({screen,state,direction,width,action:name,destination:await page.locator('#frame').getAttribute('data-screen')});
        }
      }
    }
    for(const screen of ['pk-login','pk-autofill'])for(const state of screens[screen].states) {
      await show(screen,state);
      for(const name of ['Sign in with a passkey','Change password','Sign in','Sign in','Use a passkey']) {
        const action=page.getByRole('button',{name,exact:true});
        if(await action.count())await action.click();
      }
      if(state.startsWith('connect')) {
        await page.getByRole('button',{name:'Review app permissions',exact:true}).click();
        await page.getByRole('heading',{name:'Let Farm Ledger act as you?',exact:true}).waitFor();
        await page.getByRole('button',{name:'Cancel',exact:true}).click();
        await page.getByRole('heading',{name:'Connection cancelled',exact:true}).waitFor();
      } else {
        await page.getByRole('button',{name:'Continue',exact:true}).click();
        await accountIs({app:state!=='second-passkey-only',keys:state.startsWith('off')?0:state==='second-passkey-only'?1:2,enabled:!state.startsWith('off')});
      }
      exits.push({screen,state,width,action:'sign-in-complete',destination:await page.locator('#frame').getAttribute('data-screen')});
    }
    // Complete enrollment and cancellation from every context that offers Add passkey.
    for(const [screen,states] of [['pk-account',['empty','app-only','one-owner','both','multi-address','removed','last-factor']],['pk-owner',['notice','setup']],['pk-suggestion',['offer','offer-app']]])for(const state of states) {
      const app=screen==='pk-account'?['app-only','both','multi-address','removed'].includes(state):state==='offer-app';
      const keys=screen==='pk-account'?['both','multi-address'].includes(state)?2:['empty','app-only'].includes(state)?0:1:0;
      for(const finish of ['cancel-proof','cancel-create','done']) {
        await show(screen,state);
        if(state==='notice')await page.getByRole('button',{name:'Set up now',exact:true}).click();
        const origin=await page.locator('#frame').innerText();
        await page.getByRole('button',{name:screen==='pk-owner'?'Set up a passkey':screen==='pk-suggestion'?'Create passkey':'Add passkey',exact:true}).click();
        if(finish!=='cancel-proof') {
          const continueButton=page.getByRole('button',{name:'Continue',exact:true});
          if(await continueButton.isDisabled())await page.getByRole('button',{name:'Use a passkey',exact:true}).click();
          await continueButton.click();
        }
        if(finish==='done') {
          await page.getByRole('button',{name:'Create passkey',exact:true}).click();
          await page.getByRole('button',{name:'Done',exact:true}).click();
          await accountIs({app,keys:keys+1});
        } else {
          await page.getByRole('button',{name:'Cancel',exact:true}).click();
          assert.equal(await page.locator('#frame').innerText(),origin);
        }
        exits.push({screen,state,width,action:finish,destination:await page.locator('#frame').getAttribute('data-screen')});
      }
    }
    // Authenticator setup reuses historical screens but must return to the passkey journey.
    for(const [screen,state] of [['pk-account','empty'],['pk-account','one-owner'],['pk-account','last-factor'],['pk-owner','setup'],['pk-registration','unsupported']])for(const finish of ['cancel','done']) {
      await show(screen,state);
      const origin=await page.locator('#frame').innerText();
      await page.getByRole('button',{name:screen==='pk-account'?'Set up':'Set up two-factor authentication',exact:true}).click();
      if(finish==='cancel') {
        await page.getByRole('button',{name:'Cancel',exact:true}).click();
        assert.equal(await page.locator('#frame').innerText(),origin);
      } else {
        await page.getByRole('button',{name:'Confirm code',exact:true}).click();
        await page.getByRole('button',{name:'Copy all codes',exact:true}).click();
        await page.getByRole('checkbox',{name:'I saved my recovery codes somewhere safe.',exact:true}).check();
        await page.getByRole('button',{name:'Done',exact:true}).click();
        await accountIs({app:true,keys:['one-owner','last-factor'].includes(state)?1:0});
      }
      exits.push({screen,state,width,action:`app-${finish}`,destination:await page.locator('#frame').getAttribute('data-screen')});
    }
  }
  assert(exits.length>0,'No passkey exit journeys were exercised');
  writeFileSync(join(out,'exit-walk.json'),JSON.stringify(exits,null,2)+'\n');
  console.log(`Walked ${exits.length} passkey exit journeys through their final screen.`);

  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'manifest.json'), JSON.stringify(results.filter(result => capture(result.screen)), null, 2) + '\n');
  console.log(`Checked ${results.length} frames; captured ${captures} at 1:1 into ${out}. No page errors or horizontal frame overflow. Interaction checks passed.`);
} finally {
  await browser.close();
}
