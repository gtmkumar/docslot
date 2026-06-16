// Mock-mode smoke: confirm the default (no VITE_USE_REAL_API) app still renders
// the static mock analytics + the mock approval queue, with zero console errors —
// proving the live wiring didn't perturb mock mode.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:4173';
const OUT = 'qa/shots/mock-smoke';
mkdirSync(OUT, { recursive: true });
const errors = [];
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${page.url()} :: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`${page.url()} :: PAGEERROR ${e.message}`));

  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });

  // Analytics — mock static values (whatsapp share 70% region differs from live;
  // mock funnel "Started chat" etc). Assert the mock revenue ₹8,42,000 shows.
  await page.goto(`${BASE}/analytics`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/analytics.png`, fullPage: true });
  const a = await page.locator('main').innerText();
  if (a.includes('8,42,000')) console.log('mock analytics ok: ₹8,42,000 present');
  else fail('mock analytics revenue changed (expected 8,42,000)');

  // Overview mock approval queue — count Approve buttons (mock seed: pending rows).
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  const n = await page.getByRole('button', { name: /^Approve$/i }).count();
  console.log('mock approval-queue Approve buttons:', n);
  if (n < 1) fail('mock approval queue empty (expected pending rows)');
  await page.screenshot({ path: `${OUT}/overview.png`, fullPage: true });

  await ctx.close();
  await browser.close();
  console.log('=== mock console/page errors:', errors.length, '===');
  for (const e of errors) console.log('  ', e);
  if (errors.length) fail(`${errors.length} console error(s)`);
  else console.log('=== NO console/page errors (mock) ===');
};
run().catch((e) => { console.error(e); process.exit(1); });
