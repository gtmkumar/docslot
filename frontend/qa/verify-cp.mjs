import { chromium } from 'playwright'; import { mkdirSync } from 'node:fs';
const BASE='http://localhost:5173', OUT='qa/shots/verify-cp'; mkdirSync(OUT,{recursive:true});
const errs=[]; const b=await chromium.launch();
const ctx=await b.newContext({viewport:{width:1440,height:900},deviceScaleFactor:2}); const p=await ctx.newPage();
p.on('console',m=>{if(m.type()==='error')errs.push(m.text())}); p.on('pageerror',e=>errs.push('PAGEERR '+e.message));
await p.goto(`${BASE}/login`,{waitUntil:'networkidle'});
await p.fill('#login-email','priyanka@apollocare.in');await p.fill('#login-password','reception');
await p.click('button[type=submit]'); await p.waitForURL(u=>!u.pathname.startsWith('/login'));
await p.goto(`${BASE}/care-partners`,{waitUntil:'networkidle'}); await p.waitForTimeout(1200);
await p.screenshot({path:`${OUT}/care-partners-brokers.png`});
// payouts tab
const payTab=p.getByRole('tab',{name:/payout/i}); if(await payTab.count()){await payTab.first().click(); await p.waitForTimeout(800); await p.screenshot({path:`${OUT}/care-partners-payouts.png`});}
await p.goto(`${BASE}/calendar`,{waitUntil:'networkidle'}); await p.waitForTimeout(2500);
await p.screenshot({path:`${OUT}/calendar.png`});
console.log('errors:', errs.length? errs.slice(0,5).join(' | ') : 'NONE');
await b.close();
