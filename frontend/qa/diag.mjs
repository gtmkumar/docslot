import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://localhost:4173';
const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 390, height: 844 } });
const p = await ctx.newPage();
await p.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
await p.fill('#login-email', 'priyanka@apollocare.in');
await p.fill('#login-password', 'reception');
await p.click('button[type=submit]');
await p.waitForURL((u) => !u.pathname.startsWith('/login'));
await p.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
await p.waitForTimeout(400);
const chain = await p.evaluate(() => {
  const t = document.querySelector('table');
  const out = [];
  let el = t;
  while (el && el !== document.documentElement) {
    const cs = getComputedStyle(el);
    out.push(`${el.tagName.toLowerCase()} | client=${el.clientWidth} scroll=${el.scrollWidth} ovx=${cs.overflowX} disp=${cs.display} minW=${cs.minWidth} | cls="${(typeof el.className==='string'?el.className:'').slice(0,60)}"`);
    el = el.parentElement;
  }
  return out;
});
console.log(chain.join('\n'));
await b.close();
