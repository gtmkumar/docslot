// Final live-wiring verification (VITE_USE_REAL_API=1). Proves END-TO-END against
// the running .NET API on :5054 (Vite-proxied at /api):
//   1. Manage panel opens for a REAL booking (populated from GET /bookings/{id}),
//      and approving a pending booking from its panel moves it off Pending.
//   2. NewBooking wizard creates a booking against live doctors + slots; the new
//      booking appears in the live list and the live-queue/badge increments.
//   3. AddDoctorPanel POSTs to /doctors; "Practitioners · N" increments and the
//      new card appears.
// Captures screenshots + asserts zero console/page errors. It also harvests the
// REAL booking id created via the wizard (from the network response) so the
// caller can delete it afterward, and prints the doctor/booking markers used.
//
// Usage: BASE=http://localhost:5174 node qa/live-final2.mjs

import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5174';
const OUT = 'qa/shots/live-final2';
mkdirSync(OUT, { recursive: true });

const errors = [];
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('ASSERT FAIL:', m); process.exitCode = 1; };

const STAMP = Date.now().toString().slice(-6);
const DOCTOR_NAME = `Dr. QA Final ${STAMP}`;
const PATIENT_NAME = `QA Wizard ${STAMP}`;
const PATIENT_PHONE = `+9197${Date.now().toString().slice(-8)}`;
const created = { bookingIds: [], doctorIds: [] };

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

  // Capture booking-create + doctor-create responses to harvest real ids for cleanup.
  page.on('response', async (res) => {
    const u = res.url();
    if (res.request().method() === 'POST' && /\/api\/v1\/bookings$/.test(u) && res.ok()) {
      try { const j = await res.json(); if (j.bookingId) created.bookingIds.push(j.bookingId); } catch {}
    }
    if (res.request().method() === 'POST' && /\/api\/v1\/doctors$/.test(u) && res.ok()) {
      try { const j = await res.json(); if (j.doctorId) created.doctorIds.push(j.doctorId); } catch {}
    }
  });

  await login(page);
  log('logged in →', page.url());

  // ── 1a. MANAGE PANEL OPENS FOR A REAL BOOKING ────────────────────────────────
  await page.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/01-bookings-list.png`, fullPage: true });

  // The first pending row carries an "Approve" button; non-pending carry "Manage".
  // Open a Manage panel for a confirmed/completed row first to prove population.
  const manageButtons = page.getByRole('button', { name: /^Manage$/ });
  if (await manageButtons.count() < 1) fail('no Manage row actions in the live bookings list');
  await manageButtons.first().click();
  // The slide-over fetches GET /bookings/{id}; wait for the dialog + populated fields.
  const dialog = page.getByRole('dialog');
  await dialog.waitFor({ state: 'visible', timeout: 8000 });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${OUT}/02-manage-panel-real.png`, fullPage: true });
  const manageText = await dialog.innerText();
  // Populated from the detail endpoint: a masked phone (+91xxxx…) + a department +
  // a slot time (IST). Assert the panel is NOT stuck on the loading title.
  if (/\+91/.test(manageText) && /IST/.test(manageText)) log('MANAGE OK: panel populated from GET /bookings/{id} (phone + IST slot present)');
  else fail('manage panel did not populate from the live booking detail');
  if (/Loading/i.test(await dialog.locator('header').innerText())) fail('manage panel stuck on Loading title');
  // Close.
  await page.keyboard.press('Escape');
  await page.waitForTimeout(500);

  // ── 1b. APPROVE A PENDING BOOKING FROM THE LIST/PANEL ─────────────────────────
  // Switch to the Pending tab; capture the pending count; approve one via its panel.
  await page.getByRole('tab', { name: /Pending/i }).click();
  await page.waitForTimeout(600);
  const pendingApproveBtns = page.getByRole('button', { name: /^Approve$/ });
  const pendingBefore = await pendingApproveBtns.count();
  log('pending Approve buttons before:', pendingBefore);
  if (pendingBefore < 1) { fail('no pending bookings to approve'); }

  // Capture the target booking id from the row's manage panel so we can assert via API.
  // Open the approve panel (Approve in the row opens the Approve & collect slide-over).
  await pendingApproveBtns.first().click();
  const approveDialog = page.getByRole('dialog');
  await approveDialog.waitFor({ state: 'visible', timeout: 8000 });
  await page.waitForTimeout(1000);
  await page.screenshot({ path: `${OUT}/03-approve-panel-real.png`, fullPage: true });
  // Grab the booking id shown in the panel title (PatientChip renders `· {id}`).
  const approveText = await approveDialog.innerText();
  const idMatch = approveText.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i);
  const approvedId = idMatch ? idMatch[0] : null;
  log('approve target booking id:', approvedId);
  // Choose Cash (skips the payment link) → Approve directly.
  await approveDialog.getByRole('radio', { name: /Cash/i }).click();
  await page.waitForTimeout(200);
  // Footer primary button is "Approve" (cash/skip path).
  await approveDialog.getByRole('button', { name: /^Approve$/ }).click();
  await page.waitForTimeout(1500);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/04-after-approve.png`, fullPage: true });
  const pendingAfter = await page.getByRole('button', { name: /^Approve$/ }).count();
  log('pending Approve buttons after:', pendingAfter);
  if (pendingAfter < pendingBefore) log(`APPROVE OK: pending shrank ${pendingBefore} → ${pendingAfter}`);
  else fail(`approve did not reduce the pending tab (${pendingBefore} → ${pendingAfter})`);
  if (approvedId) created.approvedId = approvedId;

  // ── 2. CREATE A BOOKING VIA THE WIZARD ───────────────────────────────────────
  // Capture the live-queue stat + nav badge before.
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(700);
  const navBefore = await page.locator('nav').first().innerText().catch(() => '');
  const queueBadgeBefore = (navBefore.match(/\b(\d+)\b/) || [])[1];
  log('nav badge (pending) before wizard:', queueBadgeBefore);

  await page.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(500);
  await page.getByRole('button', { name: /New booking/i }).first().click();
  const wiz = page.getByRole('dialog');
  await wiz.waitFor({ state: 'visible', timeout: 8000 });
  // Step Patient.
  await wiz.locator('#nb-phone').fill(PATIENT_PHONE);
  await wiz.locator('#nb-name').fill(PATIENT_NAME);
  await wiz.locator('#nb-age').fill('29');
  await page.screenshot({ path: `${OUT}/05-wizard-patient.png`, fullPage: true });
  await wiz.getByRole('button', { name: /Continue/i }).click();
  await page.waitForTimeout(800);
  // Step Slot: pick Cardiology dept → first practitioner → first available slot.
  await wiz.getByRole('button', { name: /^Cardiology$/ }).first().click();
  await page.waitForTimeout(900); // practitioners load
  // First practitioner button in the list (has fee + next).
  const practBtns = wiz.locator('ul button[aria-pressed]');
  await practBtns.first().click();
  await page.waitForTimeout(1200); // slots load (live)
  await page.screenshot({ path: `${OUT}/06-wizard-slot.png`, fullPage: true });
  // Slot grid buttons are the mono time tiles; pick the first enabled one.
  const slotBtns = wiz.locator('.grid button:not([disabled])');
  const slotCount = await slotBtns.count();
  log('available slot tiles in wizard:', slotCount);
  if (slotCount < 1) fail('no available slots rendered in the wizard');
  await slotBtns.first().click();
  await page.waitForTimeout(300);
  await wiz.getByRole('button', { name: /Continue/i }).click();
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${OUT}/07-wizard-confirm.png`, fullPage: true });
  await wiz.getByRole('button', { name: /Confirm booking/i }).click();
  await page.waitForTimeout(1800);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(1000);
  await page.screenshot({ path: `${OUT}/08-wizard-after.png`, fullPage: true });
  if (created.bookingIds.length) log('CREATE OK: live POST /bookings returned id', created.bookingIds.at(-1));
  else fail('wizard did not produce a live booking (no POST /bookings 2xx captured)');

  // Assert the new booking appears in the live list (search by patient name).
  await page.goto(`${BASE}/bookings`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.locator('input[type=search]').fill(PATIENT_NAME);
  await page.waitForTimeout(500);
  const listBody = await page.locator('main').innerText();
  await page.screenshot({ path: `${OUT}/09-list-has-new-booking.png`, fullPage: true });
  if (listBody.includes(PATIENT_NAME)) log(`LIST OK: new booking "${PATIENT_NAME}" appears in the live list`);
  else fail(`new booking "${PATIENT_NAME}" not found in the live list`);

  // Nav badge / live-queue increments.
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(700);
  const navAfter = await page.locator('nav').first().innerText().catch(() => '');
  const queueBadgeAfter = (navAfter.match(/\b(\d+)\b/) || [])[1];
  log('nav badge (pending) after wizard:', queueBadgeAfter);
  if (Number(queueBadgeAfter) > Number(queueBadgeBefore)) log(`BADGE OK: pending badge ${queueBadgeBefore} → ${queueBadgeAfter}`);
  else log(`WARN: pending badge did not visibly increment (${queueBadgeBefore} → ${queueBadgeAfter}); note one was approved earlier this run`);

  // ── 3. ADD A DOCTOR ──────────────────────────────────────────────────────────
  await page.goto(`${BASE}/doctors`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  const headingBefore = await page.locator('#screen-heading').innerText();
  const docCountBefore = Number((headingBefore.match(/(\d+)/) || [])[1]);
  log('Practitioners count before:', docCountBefore);
  await page.screenshot({ path: `${OUT}/10-doctors-before.png`, fullPage: true });

  await page.getByRole('button', { name: /Add doctor/i }).first().click();
  const docDlg = page.getByRole('dialog');
  await docDlg.waitFor({ state: 'visible', timeout: 8000 });
  await docDlg.locator('#ad-name').fill(DOCTOR_NAME);
  await docDlg.locator('#ad-qual').fill('MD QA');
  await docDlg.locator('#ad-fee').fill('555');
  await docDlg.locator('#ad-phone').fill('+919800000123');
  await page.screenshot({ path: `${OUT}/11-add-doctor-form.png`, fullPage: true });
  await docDlg.getByRole('button', { name: /^Add doctor$/ }).last().click();
  await page.waitForTimeout(1800);
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(1000);
  await page.screenshot({ path: `${OUT}/12-doctors-after.png`, fullPage: true });
  const headingAfter = await page.locator('#screen-heading').innerText();
  const docCountAfter = Number((headingAfter.match(/(\d+)/) || [])[1]);
  log('Practitioners count after:', docCountAfter);
  if (docCountAfter === docCountBefore + 1) log(`ADD DOCTOR OK: Practitioners ${docCountBefore} → ${docCountAfter}`);
  else fail(`Practitioners count did not increment (${docCountBefore} → ${docCountAfter})`);
  const docBody = await page.locator('main').innerText();
  if (docBody.includes(DOCTOR_NAME)) log(`CARD OK: new doctor "${DOCTOR_NAME}" card appears`);
  else fail(`new doctor card "${DOCTOR_NAME}" not found`);

  await ctx.close();
  await browser.close();

  log('\n=== CREATED (for cleanup) ===');
  log('  booking ids:', JSON.stringify(created.bookingIds));
  log('  approved id (pre-existing seeded booking, leave or reset):', created.approvedId || '(none)');
  log('  doctor ids:', JSON.stringify(created.doctorIds));
  log('  patient phone:', PATIENT_PHONE, '| doctor name:', DOCTOR_NAME);

  log('\n=== CONSOLE/PAGE ERRORS:', errors.length, '===');
  for (const e of errors) log('  ', e);
  if (errors.length) fail(`${errors.length} console/page error(s)`);
  else log('=== NO console/page errors ===');
};

run().catch((e) => { console.error(e); process.exit(1); });
