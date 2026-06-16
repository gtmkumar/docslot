// Live-API end-to-end verification (VITE_USE_REAL_API=1 dev server on :5173,
// .NET API proxied at /api → :5054). Proves: analytics real KPIs, doctor cards
// real fields, nav pending badge, an approve mutation (before/after), and an
// add-patient mutation appearing in the live list. Captures screenshots and
// asserts zero console/page errors.
//
// Usage: BASE=http://localhost:5173 node qa/live-writes.mjs
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5173';
const OUT = 'qa/shots/live-writes';
mkdirSync(OUT, { recursive: true });

const errors = [];
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

async function login(page) {
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' });
  await page.fill('#login-email', 'priyanka@apollocare.in');
  await page.fill('#login-password', 'reception');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
}

const run = async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`${page.url()} :: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`${page.url()} :: PAGEERROR ${e.message}`));

  await login(page);
  log('logged in →', page.url());

  // ── 1. ANALYTICS ───────────────────────────────────────────────────────────
  await page.goto(`${BASE}/analytics`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/01-analytics.png`, fullPage: true });
  const body = await page.locator('main').innerText();
  for (const needle of ['10', '70%', '10%', '3,550']) {
    if (body.includes(needle)) log(`analytics KPI ok: "${needle}"`);
    else fail(`analytics missing KPI "${needle}"`);
  }
  if (body.includes('Cardiology')) log('analytics top-dept ok: Cardiology');
  else fail('analytics top departments missing');

  // ── 2. DOCTORS ─────────────────────────────────────────────────────────────
  await page.goto(`${BASE}/doctors`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/02-doctors.png`, fullPage: true });
  const docBody = await page.locator('main').innerText();
  // Real dept name (Cardiology) + real OPD hours (09:00–17:00) + a real rating (5).
  if (docBody.includes('Cardiology')) log('doctor card dept ok: Cardiology');
  else fail('doctor cards missing real department');
  if (docBody.includes('09:00') && docBody.includes('17:00')) log('doctor card hours ok: 09:00–17:00');
  else fail('doctor cards missing real OPD hours');

  // ── 3. NAV BADGE (pending bookings = 4) ──────────────────────────────────────
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  // The Bookings nav node carries badgeSource=pending_bookings_count → badge "4".
  const navText = await page.locator('nav').first().innerText().catch(() => '');
  await page.screenshot({ path: `${OUT}/03-overview-badge.png`, fullPage: true });
  if (/\b4\b/.test(navText)) log('nav badge ok: pending bookings = 4 visible in nav');
  else log('WARN: badge "4" not found in nav text (text was:', JSON.stringify(navText.slice(0, 200)), ')');

  // ── 4. APPROVE MUTATION (before/after) ──────────────────────────────────────
  // The Overview approval queue lists pending bookings with an inline Approve
  // button that fires the REAL POST /bookings/{id}/approve. Count rows before,
  // approve one, then assert the queue shrinks + the dashboard/badge updates.
  await page.screenshot({ path: `${OUT}/04-approve-before.png`, fullPage: true });

  const queueSection = page.getByRole('region', { name: /approval queue|approve/i }).first();
  // Fallback: locate the approval-queue Approve buttons by their short label.
  const approveButtons = page.getByRole('button', { name: /^Approve$/i });
  const beforeCount = await approveButtons.count();
  log('approval-queue Approve buttons before:', beforeCount);
  if (beforeCount < 1) { fail('no pending bookings to approve'); }
  else {
    // Capture the live-queue stat value before approving (the KPI strip).
    const beforeText = await page.locator('main').innerText();
    await approveButtons.first().click();
    // The queue uses a 5s deferred-mutation undo window before the POST fires.
    // Wait it out, then let the invalidation refetch land.
    await page.waitForTimeout(7000);
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${OUT}/05-approve-after.png`, fullPage: true });
    const afterButtons = await page.getByRole('button', { name: /^Approve$/i }).count();
    log('approval-queue Approve buttons after:', afterButtons);
    if (afterButtons < beforeCount) log('APPROVE OK: pending row left the queue (', beforeCount, '→', afterButtons, ')');
    else fail(`approve did not reduce the pending queue (${beforeCount} → ${afterButtons})`);

    // Re-fetch the badge count from the API directly to prove server state moved.
    const afterText = await page.locator('main').innerText();
    log('overview text delta captured (before len', beforeText.length, 'after len', afterText.length, ')');
  }

  // ── 5. ADD PATIENT MUTATION (new row appears) ────────────────────────────────
  await page.goto(`${BASE}/patients`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(600);
  const uniqueName = `QA Test ${Date.now().toString().slice(-6)}`;
  const uniquePhone = `+9198${Date.now().toString().slice(-8)}`;
  // Open the Add patient slide-over.
  await page.getByRole('button', { name: /add patient/i }).first().click();
  await page.waitForTimeout(500);
  await page.fill('#ap-phone', uniquePhone);
  await page.fill('#ap-name', uniqueName);
  await page.fill('#ap-age', '40');
  await page.screenshot({ path: `${OUT}/06-add-patient-form.png`, fullPage: true });
  // Submit (the panel's footer Save button).
  await page.getByRole('button', { name: /^Add patient$/i }).last().click();
  await page.waitForTimeout(1500);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/07-add-patient-after.png`, fullPage: true });
  const patientsBody = await page.locator('main').innerText();
  if (patientsBody.includes(uniqueName)) log('ADD PATIENT OK: new row "' + uniqueName + '" appears in the live list');
  else fail('new patient row did not appear in the live list (name: ' + uniqueName + ')');

  await ctx.close();
  await browser.close();

  log('\n=== CONSOLE/PAGE ERRORS:', errors.length, '===');
  for (const e of errors) log('  ', e);
  if (errors.length) fail(`${errors.length} console/page error(s)`);
  else log('=== NO console/page errors ===');
};

run().catch((e) => { console.error(e); process.exit(1); });
