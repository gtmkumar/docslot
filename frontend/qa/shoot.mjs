// DocSlot live-QA screenshot harness.
// Logs in with the demo account, then captures every route at 3 viewports.
// Usage: BASE=http://localhost:4173 node qa/shoot.mjs [tag]
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:4173';
const TAG = process.argv[2] || 'run';
const OUT = `qa/shots/${TAG}`;
mkdirSync(OUT, { recursive: true });

const VIEWPORTS = [
  { name: 'mobile', width: 390, height: 844 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'desktop', width: 1440, height: 900 },
];

const ROUTES = [
  ['overview', '/'],
  ['bookings', '/bookings'],
  ['calendar', '/calendar'],
  ['doctors', '/doctors'],
  ['patients', '/patients'],
  ['analytics', '/analytics'],
  ['team', '/team'],
  ['developers', '/developers'],
  ['security', '/security'],
  ['care-partners', '/care-partners'],
  ['settings', '/settings'],
];

const consoleErrors = [];

async function login(page) {
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
}

const run = async () => {
  const browser = await chromium.launch();
  for (const vp of VIEWPORTS) {
    const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height }, deviceScaleFactor: 2 });
    const page = await ctx.newPage();
    page.on('console', (m) => {
      if (m.type() === 'error') consoleErrors.push(`[${vp.name}] ${page.url()} :: ${m.text()}`);
    });
    page.on('pageerror', (e) => consoleErrors.push(`[${vp.name}] ${page.url()} :: PAGEERROR ${e.message}`));
    await login(page);
    for (const [name, path] of ROUTES) {
      await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
      await page.waitForTimeout(450);
      await page.screenshot({ path: `${OUT}/${name}-${vp.name}.png`, fullPage: true });
      console.log(`shot ${name} @ ${vp.name}`);
    }
    await ctx.close();
  }
  await browser.close();
  if (consoleErrors.length) {
    console.log(`\n=== CONSOLE/PAGE ERRORS (${consoleErrors.length}) ===`);
    for (const e of consoleErrors) console.log(e);
  } else {
    console.log('\n=== NO console/page errors ===');
  }
};

run().catch((e) => { console.error(e); process.exit(1); });
