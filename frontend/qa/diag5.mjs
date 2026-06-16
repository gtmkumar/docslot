import { chromium } from 'playwright';
const BASE='http://localhost:4173';
const b=await chromium.launch();
const ctx=await b.newContext({viewport:{width:390,height:844}});
const p=await ctx.newPage();
await p.goto(`${BASE}/login`,{waitUntil:'networkidle'});
await p.fill('#login-email','priyanka@apollocare.in');await p.fill('#login-password','reception');
await p.click('button[type=submit]'); await p.waitForURL(u=>!u.pathname.startsWith('/login'));
await p.goto(`${BASE}/`,{waitUntil:'networkidle'}); await p.waitForTimeout(600);
const r=await p.evaluate(()=>{
  // find the approval queue: a button containing the manage short text
  const btns=[...document.querySelectorAll('button')];
  const manage=btns.find(b=>/manage/i.test(b.textContent));
  const li=manage?.closest('li');
  const card=li?.closest('[class*="rounded"]');
  const actions=manage?.parentElement;
  const rb=(e)=>e?Math.round(e.getBoundingClientRect().right):null;
  const lb=(e)=>e?Math.round(e.getBoundingClientRect().left):null;
  return { win: window.innerWidth, manageRight: rb(manage), approveRight: rb(actions?.lastElementChild), actionsLeft: lb(actions), liRight: rb(li), liLeft: lb(li), cardRight: rb(card), cardLeft: lb(card), cardOverflowHidden: card?getComputedStyle(card).overflowX:null, liScroll: li?li.scrollWidth:null, liClient: li?li.clientWidth:null };
});
console.log(JSON.stringify(r,null,1));
await b.close();
