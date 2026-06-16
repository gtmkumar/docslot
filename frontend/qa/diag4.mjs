import { chromium } from 'playwright';
const BASE = 'http://localhost:4173';
const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 390, height: 844 } });
const p = await ctx.newPage();
await p.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
await p.fill('#login-email','priyanka@apollocare.in'); await p.fill('#login-password','reception');
await p.click('button[type=submit]');
await p.waitForURL((u)=>!u.pathname.startsWith('/login'));
await p.goto(`${BASE}/`, { waitUntil: 'networkidle' });
await p.waitForTimeout(500);
const r = await p.evaluate(() => {
  const out = {};
  out.docW = document.documentElement.scrollWidth; out.win = window.innerWidth;
  const header = document.querySelector('header');
  out.headerScroll = header.scrollWidth; out.headerClient = header.clientWidth;
  out.buttons = [...header.querySelectorAll('button, a')].map(el => ({ txt: el.textContent.trim().slice(0,18), disp: getComputedStyle(el).display, w: Math.round(el.getBoundingClientRect().width) })).filter(x=>x.disp!=='none');
  // matchMedia checks
  out.mqLg = window.matchMedia('(min-width:1024px)').matches;
  out.mqMd = window.matchMedia('(min-width:768px)').matches;
  return out;
});
console.log(JSON.stringify(r,null,1));
await b.close();
