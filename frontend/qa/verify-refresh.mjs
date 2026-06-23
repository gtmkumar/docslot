// Verifies the 401 → refresh → retry interceptor in src/lib/api-client.ts.
// Flow: log in as platform-admin (valid tokens land in localStorage), then
// CORRUPT the access token while keeping the real refresh token, reload, and
// assert the app recovers transparently:
//   - boot requests (/me, /me/permissions, /me/menus, /me/badges) 401 once then 200
//   - exactly ONE POST /auth/refresh fires for the burst (single-flight; refresh
//     tokens rotate, so parallel refreshes would kill the session)
//   - the dashboard renders (no bounce to /login)
//
// Usage: BASE=http://localhost:5200 node qa/verify-refresh.mjs
import { chromium } from 'playwright';

const BASE = process.env.BASE || 'http://localhost:5200';
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };
const ok = (m) => console.log('OK  ', m);

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await ctx.newPage();

  // ── 1. Log in (real tokens persisted to localStorage 'docslot.session') ──────
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'admin@docslot.io');
  await page.fill('#login-password', 'admin');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
  ok('logged in as platform-admin');

  // ── 2. Corrupt ONLY the access token; keep the valid (rotating) refresh token ─
  const before = await page.evaluate(() => {
    const raw = JSON.parse(localStorage.getItem('docslot.session'));
    const rt = raw.state.refreshToken;
    raw.state.accessToken = 'expired.invalid.token';
    localStorage.setItem('docslot.session', JSON.stringify(raw));
    return { hadRefresh: Boolean(rt) };
  });
  if (!before.hadRefresh) return fail('no refresh token persisted after login');
  ok('access token corrupted, refresh token kept');

  // ── 3. Record network, reload, let the boot burst fly ────────────────────────
  const seen = []; // { url, status }
  page.on('response', (r) => {
    const u = r.url();
    if (u.includes('/api/v1/')) seen.push({ url: u.replace(/^.*\/api\/v1/, ''), status: r.status() });
  });
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);

  // ── 4. Assertions ────────────────────────────────────────────────────────────
  const refreshes = seen.filter((s) => s.url.startsWith('/auth/refresh'));
  const me = seen.filter((s) => s.url === '/me');
  const had401 = seen.some((s) => s.status === 401);
  const meOk = me.some((s) => s.status === 200);

  if (!had401) fail('expected at least one initial 401 from the corrupted token');
  else ok('saw initial 401(s) from the stale token');

  if (refreshes.length === 0) fail('no /auth/refresh fired — interceptor did not run');
  else if (refreshes.length > 1) fail(`single-flight broken: ${refreshes.length} /auth/refresh calls (rotation would kill the session)`);
  else ok('exactly ONE /auth/refresh (single-flight) — status ' + refreshes[0].status);

  if (!meOk) fail('/me never returned 200 after refresh');
  else ok('/me recovered to 200 after refresh+retry');

  const onApp = await page.evaluate(() => !location.pathname.startsWith('/login'));
  const navVisible = await page.locator('nav').first().isVisible().catch(() => false);
  if (!onApp || !navVisible) fail('did not land on the authed app shell');
  else ok('dashboard rendered (stayed on authed shell)');

  // post-recovery, no lingering 401 for the core reads
  const lingering = ['/me', '/me/permissions', '/me/menus'].filter(
    (p) => seen.some((s) => s.url === p && s.status === 401) && !seen.some((s) => s.url === p && s.status === 200),
  );
  if (lingering.length) fail('these never recovered to 200: ' + lingering.join(', '));
  else ok('core reads (/me, /permissions, /menus) all recovered');

  await browser.close();
  console.log(process.exitCode ? '\nRESULT: FAIL' : '\nRESULT: PASS');
};

run().catch((e) => { console.error(e); process.exit(1); });
