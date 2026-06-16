// Detects horizontal overflow (document wider than viewport) per route × viewport.
import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://localhost:4173';
const VPS = [
  { name: 'mobile', width: 390, height: 844 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'desktop', width: 1440, height: 900 },
];
const ROUTES = ['/', '/bookings', '/calendar', '/doctors', '/patients', '/analytics', '/team', '/developers', '/security', '/care-partners', '/settings'];

const browser = await chromium.launch();
const findings = [];
for (const vp of VPS) {
  const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
  const page = await ctx.newPage();
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
  for (const r of ROUTES) {
    await page.goto(`${BASE}${r}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(350);
    const m = await page.evaluate(() => ({
      docW: document.documentElement.scrollWidth,
      winW: window.innerWidth,
      // find the widest offending element
      wide: [...document.querySelectorAll('*')]
        .filter((el) => el.scrollWidth > window.innerWidth + 1 && el.getBoundingClientRect().width > window.innerWidth + 1)
        .slice(0, 3)
        .map((el) => `${el.tagName.toLowerCase()}.${(el.className && typeof el.className === 'string' ? el.className.split(' ').slice(0,3).join('.') : '')} (w=${Math.round(el.getBoundingClientRect().width)})`),
    }));
    const overflow = m.docW > m.winW + 1;
    if (overflow) findings.push(`OVERFLOW ${r} @ ${vp.name}: doc=${m.docW} win=${m.winW} :: ${m.wide.join(' | ')}`);
  }
  await ctx.close();
}
await browser.close();
if (findings.length) { console.log('=== HORIZONTAL OVERFLOW FOUND ==='); findings.forEach((f) => console.log(f)); }
else console.log('=== NO horizontal overflow on any route × viewport ===');
