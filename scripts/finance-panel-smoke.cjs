'use strict';
const notificationFixture = require('./notification-fixture.cjs');
// Synthetic API contract only; no sessions, SMTP, production database or real tokens.
const http=require('node:http'), fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const dependencies=path.resolve(__dirname,'../expense-tracker-ui');
const {chromium}=require(require.resolve('playwright',{paths:[dependencies]}));
const AxeBuilder=require(require.resolve('@axe-core/playwright',{paths:[dependencies]})).default;
(async()=>{
 const root=path.resolve(__dirname,'../expense-tracker-ui/dist');
 const server=http.createServer((request,response)=>{
  const name=new URL(request.url,'http://localhost').pathname;
  const file=path.resolve(root,'.'+(name==='/'?'/index.html':name));
  if(!file.startsWith(root+path.sep)){response.writeHead(404).end();return;}
  fs.readFile(file,(error,data)=>{if(error){response.writeHead(404).end();return;}response.setHeader('Content-Type',file.endsWith('.js')?'application/javascript':file.endsWith('.css')?'text/css':file.endsWith('.json')?'application/json':file.endsWith('.svg')?'image/svg+xml':'text/html');response.end(data);});
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true});
 let checks=0;
 try{
  for(const width of [1440,390,320]){
   const context=await browser.newContext({viewport:{width,height:900},serviceWorkers:'block'});
   const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
   let tokens=[];const fake='fin_'+'a'.repeat(43);const id='00000001-0000-0000-0000-000000000000';
   await page.route('**/api/**',async route=>{
    const request=route.request(),url=new URL(request.url());let data={};let status=200;
    if (url.pathname === '/api/account/mfa') data = { enabled: false, available: true, recoveryCodesRemaining: 0, pendingSetupId: null };
    else if(url.pathname.startsWith('/api/account/notifications'))data=notificationFixture(url.pathname);
    else if(url.pathname==='/api/auth/me')data={username:'synthetic-user',accountContext:'A'.repeat(64)};
    else if(url.pathname==='/api/account')data={username:'synthetic-user',email:'fixture@example.invalid',emailVerified:true,createdAt:new Date().toISOString()};
    else if(url.pathname==='/api/account/finance-tokens'){
     if(request.method()==='POST'){tokens=[{id,createdAt:new Date().toISOString(),expiresAt:new Date(Date.now()+86400000).toISOString()}];data={id,token:fake,expiresAt:tokens[0].expiresAt};}
     else data={available:true,tokens};
    }else if(url.pathname.startsWith('/api/account/finance-tokens/')){tokens=[];data=null;status=204;}
    else if(url.pathname==='/api/subscriptions')data={items:[],total:0};
    else if(url.pathname==='/api/subscriptions/summary')data={byCurrency:[],activeSubscriptions:0,totalSubscriptions:0,dueThisWeek:0};
    else if(url.pathname==='/api/subscriptions/rates')data={RON:1,EUR:.2,USD:.22};
    await route.fulfill({status,contentType:'application/json',body:status===204?'':JSON.stringify(data)});
   });
   await page.goto(`http://127.0.0.1:${server.address().port}`);
   await page.getByRole('button',{name:'Setări cont'}).click();
   await page.getByLabel('Confirmă parola curentă').fill('synthetic-integration-passphrase');
   await page.getByRole('button',{name:'Creează token de conectare'}).click();
   const token=page.getByLabel('Token Finance');await token.waitFor();
   assert.equal(await token.inputValue(),fake);checks++;
   assert.equal(await token.getAttribute('type'),'password');checks++;
   await page.getByRole('button',{name:'Arată tokenul',exact:true}).click();
   assert.equal(await token.getAttribute('type'),'text');checks++;
   assert.equal(await page.evaluate(()=>JSON.stringify(localStorage).includes('fin_')),false);checks++;
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),JSON.stringify(await page.evaluate(()=>({width:innerWidth,overflow:[...document.querySelectorAll('body *')].filter(el=>el.getBoundingClientRect().right>innerWidth+1&&el.getBoundingClientRect().width>0).slice(0,12).map(el=>({tag:el.tagName,classes:el.className,right:el.getBoundingClientRect().right,width:el.getBoundingClientRect().width}))}))));checks++;
   const audit=await new AxeBuilder({page}).include('section[aria-labelledby="finance-integration-title"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)})),[]);checks++;
   page.once('dialog',dialog=>dialog.accept());await page.getByRole('button',{name:'Revocă',exact:true}).click();
   await token.waitFor({state:'detached'});await page.getByRole('button',{name:'Revocă',exact:true}).waitFor({state:'detached'});assert.equal(await page.getByRole('button',{name:'Revocă',exact:true}).count(),0);checks++;
   assert.deepEqual(errors,[]);checks++;
   await context.close();
  }
  console.log(`PASS: ${checks} C# connector UI assertions at 1440/390/320 px, including WCAG, reveal, revocation and no browser credential storage`);
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
