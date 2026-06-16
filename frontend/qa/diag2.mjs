import { chromium } from 'playwright';
const BASE = 'http://localhost:4173';
const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 390, height: 844 } });
const p = await ctx.newPage();
await p.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
await p.fill('#login-email', 'priyanka@apollocare.in');
await p.fill('#login-password', 'reception');
await p.click('button[type=submit]');
await p.waitForURL((u) => !u.pathname.startsWith('/login'));
await p.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
for (const w of [100, 350, 700, 1200]) {
  await p.waitForTimeout(w === 100 ? 100 : w - (w===350?100:w===700?350:700));
  const m = await p.evaluate(() => ({ de: document.documentElement.scrollWidth, body: document.body.scrollWidth, win: window.innerWidth, hasTable: !!document.querySelector('table'), hasSkel: !!document.querySelector('[aria-busy="true"]') }));
  console.log(`@~${w}ms`, JSON.stringify(m));
}
await b.close();
