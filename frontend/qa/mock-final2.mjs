// Mock-mode regression: with the flag OFF (default), the manage slide-over still
// opens for a prototype booking and the NewBooking wizard still completes — proving
// the live-wiring changes left mock behavior intact. No DB; pure mock seam.
// Usage: BASE=http://localhost:5173 node qa/mock-final2.mjs
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5173';
const OUT = 'qa/shots/mock-final2';
mkdirSync(OUT, { recursive: true });
const errors = [];
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

async function login(page) {
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
}

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${page.url()} :: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`${page.url()} :: PAGEERROR ${e.message}`));
  await login(page);

  // Manage panel opens for a prototype booking (B-2838 is confirmed → Manage).
  await page.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  await page.getByRole('button', { name: /^Manage$/ }).first().click();
  const dlg = page.getByRole('dialog');
  await dlg.waitFor({ state: 'visible', timeout: 8000 });
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/01-manage-mock.png`, fullPage: true });
  const txt = await dlg.innerText();
  if (/\+91/.test(txt) && /IST/.test(txt)) log('MOCK MANAGE OK: panel populated from prototype seam');
  else fail('mock manage panel did not populate');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(400);

  // Wizard completes end-to-end on the mock seam.
  await page.getByRole('button', { name: /New booking/i }).first().click();
  const wiz = page.getByRole('dialog');
  await wiz.waitFor({ state: 'visible', timeout: 8000 });
  await wiz.locator('#nb-phone').fill('+91 98765 43210');
  await wiz.locator('#nb-name').fill('Mock Patient');
  await wiz.locator('#nb-age').fill('30');
  await wiz.getByRole('button', { name: /Continue/i }).click();
  await page.waitForTimeout(500);
  await wiz.getByRole('button', { name: /^Cardiology$/ }).first().click();
  await page.waitForTimeout(500);
  await wiz.locator('ul button[aria-pressed]').first().click();
  await page.waitForTimeout(600);
  await wiz.locator('.grid button:not([disabled])').first().click();
  await page.waitForTimeout(300);
  await wiz.getByRole('button', { name: /Continue/i }).click();
  await page.waitForTimeout(400);
  await wiz.getByRole('button', { name: /Confirm booking/i }).click();
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${OUT}/02-wizard-mock.png`, fullPage: true });
  log('MOCK WIZARD OK: completed without error');

  await ctx.close();
  await browser.close();
  log('\n=== CONSOLE/PAGE ERRORS:', errors.length, '===');
  for (const e of errors) log('  ', e);
  if (errors.length) fail(`${errors.length} console/page error(s)`);
  else log('=== NO console/page errors ===');
};
run().catch((e) => { console.error(e); process.exit(1); });
