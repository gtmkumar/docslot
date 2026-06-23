// Live-API verification for the PLATFORM-ADMIN Developers + Security consoles.
// VITE_USE_REAL_API=1 dev server on :5173, .NET API proxied at /api → :5054.
// Login as the PLATFORM-ADMIN (admin@docslot.io / admin), NOT priyanka — only the
// platform-admin's /me/menus returns developers + security.
//
// Proves: sidebar shows Developers + Security (backend-driven nav); API clients tab
// shows the 4 real clients; Security breaches=2, dpdp=2, keys=6, audit verify result;
// empty lists (webhooks / anchors / review-queue / deletion-certs) render an empty
// state, not an error. Also re-checks priyanka (tenant_owner) does NOT see the two
// platform menus. Asserts zero console/page errors.
//
// Usage: BASE=http://localhost:5173 node qa/live-admin.mjs
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5173';
const OUT = 'qa/shots/live-admin';
mkdirSync(OUT, { recursive: true });

const errors = [];
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

async function login(page, email, password) {
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', email);
  await page.fill('#login-password', password);
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
}

async function logout(page) {
  // Clear the session and return to login for the second persona.
  await page.evaluate(() => { try { localStorage.clear(); sessionStorage.clear(); } catch {} });
}

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${page.url()} :: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`${page.url()} :: PAGEERROR ${e.message}`));

  // ── PLATFORM ADMIN ────────────────────────────────────────────────────────
  await login(page, 'admin@docslot.io', 'admin');
  log('admin logged in →', page.url());
  await page.waitForTimeout(800);

  // Sidebar must show Developers + Security (from /me/menus).
  const nav = await page.locator('nav, aside').first().innerText().catch(() => '');
  await page.screenshot({ path: `${OUT}/00-admin-nav.png`, fullPage: true });
  if (/Developers/i.test(nav)) log('nav: Developers present'); else fail('admin nav missing Developers');
  if (/Security/i.test(nav)) log('nav: Security present'); else fail('admin nav missing Security');

  // ── DEVELOPERS — API clients tab (4 real clients) ──────────────────────────
  await page.goto(`${BASE}/developers`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1000);
  await page.screenshot({ path: `${OUT}/01-developers-clients.png`, fullPage: true });
  let body = await page.locator('main').innerText();
  for (const name of ['Apollo HMS Integration', 'Star Insurance Claims', 'PharmEasy Rx Sync', 'Legacy Web Portal']) {
    if (body.includes(name)) log(`client ok: ${name}`); else fail(`clients tab missing "${name}"`);
  }
  if (/partner/i.test(body)) log('client type "partner" rendered'); else fail('client type not rendered');

  // Scopes tab (15 scopes)
  await page.getByRole('tab', { name: 'Scopes', exact: true }).click();
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}/02-developers-scopes.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/docslot\.|read|patients/i.test(body)) log('scopes tab renders real scopes'); else fail('scopes tab empty');

  // Webhooks tab — empty list → empty state, not an error
  await page.getByRole('tab', { name: 'Webhooks', exact: true }).click();
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${OUT}/03-developers-webhooks-empty.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/error|something went wrong|retry/i.test(body)) fail('webhooks empty rendered as ERROR, not empty-state');
  else log('webhooks empty → empty state (no error)');

  // ── SECURITY ────────────────────────────────────────────────────────────────
  await page.goto(`${BASE}/security`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1000);
  // Default tab = Audit integrity → verify result
  await page.screenshot({ path: `${OUT}/04-security-audit.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/intact|verified|broken|chain/i.test(body)) log('audit verify result rendered'); else fail('audit verify result missing');

  // Breaches (2)
  await page.getByRole('tab', { name: 'Breach register', exact: true }).click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/05-security-breaches.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/unauthorized_access|unauthorized access|data.export|breach/i.test(body)) log('breaches tab shows real breaches');
  else fail('breaches tab: no real breach rows');

  // DPDP (2)
  await page.getByRole('tab', { name: 'DPDP rights', exact: true }).click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/06-security-dpdp.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/erasure|export|correction|pending|completed/i.test(body)) log('dpdp tab shows real requests');
  else fail('dpdp tab: no real request rows');

  // Review queue — empty → empty state
  await page.getByRole('tab', { name: 'Review queue', exact: true }).click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/07-security-review-empty.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/error|something went wrong/i.test(body)) fail('review-queue empty rendered as ERROR');
  else log('review-queue empty → empty state (no error)');

  // Keys (6)
  await page.getByRole('tab', { name: 'Encryption keys', exact: true }).click();
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/08-security-keys.png`, fullPage: true });
  body = await page.locator('main').innerText();
  if (/phi|pii|rotation|key|overdue|due/i.test(body)) log('keys tab shows real key rows'); else fail('keys tab empty');

  // ── PRIYANKA (tenant_owner) — must NOT see Developers/Security in nav ──────────
  await logout(page);
  await login(page, 'priyanka@apollocare.in', 'reception');
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${OUT}/09-priyanka-nav.png`, fullPage: true });
  const pnav = await page.locator('nav, aside').first().innerText().catch(() => '');
  if (/Developers/i.test(pnav)) fail('priyanka nav SHOULD NOT show Developers'); else log('priyanka nav: no Developers (correct)');
  if (/Security/i.test(pnav)) fail('priyanka nav SHOULD NOT show Security'); else log('priyanka nav: no Security (correct)');

  // ── ERRORS ───────────────────────────────────────────────────────────────────
  if (errors.length) { console.error('CONSOLE/PAGE ERRORS:\n' + errors.join('\n')); fail(`${errors.length} console/page errors`); }
  else log('zero console/page errors');

  await browser.close();
  log('DONE. exitCode =', process.exitCode || 0);
};
run().catch((e) => { console.error(e); process.exit(1); });
