import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
const BASE='http://localhost:5173', OUT='qa/shots/verify-ro'; mkdirSync(OUT,{recursive:true});
const errs=[]; const b=await chromium.launch();
const ctx=await b.newContext({viewport:{width:1440,height:900},deviceScaleFactor:2});
const p=await ctx.newPage();
p.on('console',m=>{if(m.type()==='error')errs.push(m.text())});
p.on('pageerror',e=>errs.push('PAGEERR '+e.message));
await p.goto(`${BASE}/login`,{waitUntil:'networkidle'});
await p.fill('#login-email','priyanka@apollocare.in');await p.fill('#login-password','reception');
await p.click('button[type=submit]'); await p.waitForURL(u=>!u.pathname.startsWith('/login'));
// 1) Bookings → open Manage on a real booking (read-only)
await p.goto(`${BASE}/bookings`,{waitUntil:'networkidle'}); await p.waitForTimeout(800);
await p.locator('tbody tr').first().getByText(/Manage|Approve/).first().click();
await p.waitForSelector('[role=dialog]',{timeout:8000}); await p.waitForTimeout(700);
await p.screenshot({path:`${OUT}/manage-real-booking.png`});
const dlgText=await p.locator('[role=dialog]').innerText();
console.log('panel opened, has IST slot:', /IST/.test(dlgText), '| has booking #:', /B|BKG|#/.test(dlgText));
await p.keyboard.press('Escape'); await p.waitForTimeout(300);
// 2) New booking wizard → step to slots (read-only, no submit)
await p.goto(`${BASE}/bookings?panel=newBooking`,{waitUntil:'networkidle'}); await p.waitForTimeout(900);
await p.screenshot({path:`${OUT}/wizard-step1.png`});
console.log('errors:', errs.length? errs.join(' | ') : 'NONE');
await b.close();
