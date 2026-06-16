import { chromium } from 'playwright';
const BASE='http://localhost:4173';
const b=await chromium.launch();
const ctx=await b.newContext({viewport:{width:1440,height:900},deviceScaleFactor:2});
const p=await ctx.newPage();
let err=[]; p.on('pageerror',e=>err.push(e.message));
await p.goto(`${BASE}/login`,{waitUntil:'networkidle'});
await p.fill('#login-email','priyanka@apollocare.in');await p.fill('#login-password','reception');
await p.click('button[type=submit]'); await p.waitForURL(u=>!u.pathname.startsWith('/login'));
await p.goto(`${BASE}/bookings`,{waitUntil:'networkidle'}); await p.waitForTimeout(400);
// click the first visible "Manage" button anywhere in the table
await p.locator('tbody').getByText('Manage',{exact:true}).first().click();
await p.waitForSelector('[role=dialog]',{timeout:5000});
await p.waitForTimeout(300);
await p.screenshot({path:'qa/shots/panels/manageAppointment-desktop.png'});
console.log('manage panel opened OK', err.length?('ERRORS:'+err.join('|')):'(no errors)');
await b.close();
