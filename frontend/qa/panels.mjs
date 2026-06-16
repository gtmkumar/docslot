// Functional test: click real triggers, assert the slide-over opens, screenshot.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
const BASE = 'http://localhost:4173';
const OUT = 'qa/shots/panels';
mkdirSync(OUT, { recursive: true });

const cases = [
  { name: 'newBooking', route: '/bookings', click: 'text=+ New booking' },
  { name: 'manageAppointment', route: '/bookings', click: 'tbody tr:first-child >> text=Manage' },
  { name: 'addDoctor', route: '/doctors', click: 'text=Add doctor' },
  { name: 'bookTime', route: '/calendar', click: 'header >> text=Book time' },
  { name: 'inviteUser', route: '/team', click: 'text=Invite user' },
];

const errors = [];
const browser = await chromium.launch();
for (const vp of [{ n: 'desktop', w: 1440, h: 900 }, { n: 'mobile', w: 390, h: 844 }]) {
  const ctx = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('pageerror', (e) => errors.push(`${vp.n} PAGEERROR ${e.message}`));
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'));
  for (const c of cases) {
    try {
      await page.goto(`${BASE}${c.route}`, { waitUntil: 'networkidle' });
      await page.waitForTimeout(400);
      await page.click(c.click, { timeout: 5000 });
      // A slide-over is a dialog; wait for role=dialog
      await page.waitForSelector('[role=dialog]', { timeout: 5000 });
      await page.waitForTimeout(350);
      await page.screenshot({ path: `${OUT}/${c.name}-${vp.n}.png` });
      // check overflow while panel open
      const ov = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
      console.log(`OK ${c.name} @ ${vp.n}${ov ? ' [OVERFLOW!]' : ''}`);
    } catch (e) {
      console.log(`FAIL ${c.name} @ ${vp.n}: ${e.message.split('\n')[0]}`);
    }
  }
  await ctx.close();
}
await browser.close();
if (errors.length) { console.log('=== ERRORS ==='); errors.forEach((e) => console.log(e)); }
else console.log('=== no page errors ===');
