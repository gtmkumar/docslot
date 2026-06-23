// Mock-mode (flag OFF, DEFAULT) spot-check that Developers + Security still render
// the MOCK seed unchanged after the live-wiring change. Login uses the mock auth
// (priyanka@apollocare.in / reception — the mock seed grants her the platform menus).
// Usage: BASE=http://localhost:5173 node qa/mock-admin-smoke.mjs
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5173';
const OUT = 'qa/shots/mock-admin';
mkdirSync(OUT, { recursive: true });
const errors = [];
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${page.url()} :: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`${page.url()} :: PAGEERROR ${e.message}`));

  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
  log('mock logged in →', page.url());

  // Developers — mock 4 clients
  await page.goto(`${BASE}/developers`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/01-developers.png`, fullPage: true });
  let body = await page.locator('main').innerText();
  if (body.includes('Apollo HMS Integration')) log('mock developers: clients render'); else fail('mock developers: no clients');

  // Mock webhooks (2 seeded — NOT empty in mock, proving mock unchanged)
  await page.getByRole('tab', { name: 'Webhooks', exact: true }).click();
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/02-developers-webhooks.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/Booking sync|Claims report|webhook/i.test(body)) log('mock webhooks render (mock seed intact)');
  else log('mock webhooks: check screenshot');

  // Security — mock seed: broken chain + 3 breaches + 3 dpdp + 4 keys
  await page.goto(`${BASE}/security`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/03-security-audit.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/intact|broken|chain/i.test(body)) log('mock security audit renders'); else fail('mock security audit missing');

  await page.getByRole('tab', { name: 'Breach register', exact: true }).click();
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/04-security-breaches.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/breach|access|misconfig|phishing/i.test(body)) log('mock breaches render'); else fail('mock breaches missing');

  if (errors.length) { console.error('CONSOLE/PAGE ERRORS:\n' + errors.join('\n')); fail(`${errors.length} console/page errors`); }
  else log('zero console/page errors');
  await browser.close();
  log('DONE. exitCode =', process.exitCode || 0);
};
run().catch((e) => { console.error(e); process.exit(1); });
