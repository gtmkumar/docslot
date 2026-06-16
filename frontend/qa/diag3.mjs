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
await p.waitForTimeout(500);
const info = await p.evaluate(() => {
  const res = [];
  for (const el of document.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (r.right > window.innerWidth + 1) {
      const cs = getComputedStyle(el);
      res.push({ tag: el.tagName.toLowerCase(), cls: (typeof el.className==='string'?el.className:'').slice(0,50), pos: cs.position, left: Math.round(r.left), right: Math.round(r.right), w: Math.round(r.width), parent: el.parentElement?.tagName.toLowerCase()+'.'+((typeof el.parentElement?.className==='string'?el.parentElement.className:'')||'').slice(0,30) });
    }
  }
  return res.slice(0, 12);
});
console.log(JSON.stringify(info, null, 1));
await b.close();
