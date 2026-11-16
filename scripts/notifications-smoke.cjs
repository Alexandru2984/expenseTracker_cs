'use strict';
const http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const deps=path.resolve(__dirname,'../expense-tracker-ui'),root=path.join(deps,'dist');
const {chromium}=require(require.resolve('playwright',{paths:[deps]}));
const AxeBuilder=require(require.resolve('@axe-core/playwright',{paths:[deps]})).default;
const defaults=require('./notification-fixture.cjs');
const literal='<img src=x onerror="window.__mailXss=true">';
const username='N'.repeat(50),email='a'.repeat(64)+'@'+'d'.repeat(60)+'.example.invalid';
(async()=>{
 const server=http.createServer((req,res)=>{
  const pathname=new URL(req.url,'http://localhost').pathname,file=path.resolve(root,'.'+(pathname==='/'?'/index.html':pathname));
  if(!file.startsWith(root+path.sep)){res.writeHead(404).end();return;}
  fs.readFile(file,(error,data)=>{if(error){res.writeHead(404).end();return;}res.setHeader('Content-Type',file.endsWith('.js')?'application/javascript':file.endsWith('.css')?'text/css':file.endsWith('.svg')?'image/svg+xml':'text/html');res.end(data);});
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true});let checks=0;
 try{
  for(const width of (process.env.EXPENSE_UI_WIDTHS??'1440,390,320').split(',').map(Number))for(const theme of ['light','dark']){
   const context=await browser.newContext({viewport:{width,height:950},serviceWorkers:'block',reducedMotion:'reduce'});
   await context.addInitScript(theme=>localStorage.setItem('theme',theme),theme);
   const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
   let preferences={...defaults('/api/account/notifications'),available:true,recipient:email},initialFailure=1,conflict=1,revision=0,authed=true,previewReads=0,slow=false,releasePreview,previewEntered;
   const gate=new Promise(resolve=>{releasePreview=resolve;}),entered=new Promise(resolve=>{previewEntered=resolve;});
   const recent=[{localDate:'2026-11-16',state:'Uncertain',attempts:1},{localDate:'2026-11-15',state:'Sent',attempts:1},{localDate:'2026-11-14',state:'Failed',attempts:3}];
   await context.route('https://analytics.micutu.com/**',route=>route.abort());
   await context.route('**/api/**',async route=>{
    const req=route.request(),p=new URL(req.url()).pathname;let data={},status=200;
    if(p.startsWith('/api/account/notifications')){
     assert.equal(req.headers()['x-account-context'],'A'.repeat(64));
     if(p==='/api/account/notifications'&&req.method()==='GET'){
      if(initialFailure-->0){status=503;data={title:'Preferințe temporar indisponibile.'};}else data=preferences;
     }else if(p==='/api/account/notifications'&&req.method()==='PUT'){
      const body=req.postDataJSON();
      assert.equal(req.headers()['x-notification-recipient'],preferences.recipientContext);
      if(conflict-->0){preferences.version='11111111-1111-1111-1111-'+String(++revision).padStart(12,'0');status=409;data={title:'Preferințe modificate. Reîncarcă-le.'};}
      else{
       assert.equal(body.version,preferences.version);preferences={...preferences,...body,version:'11111111-1111-1111-1111-'+String(++revision).padStart(12,'0'),
        nextDigestAtUtc:body.enabled?new Date(Date.now()+86400000).toISOString():null};data=preferences;
      }
     }else if(p.endsWith('/time-zones'))data=['UTC','Europe/Bucharest','America/New_York'];
     else if(p.endsWith('/history'))data=recent;
     else if(p.endsWith('/preview')){
      previewReads++;if(slow){previewEntered();await gate;}
      data={localDate:new Date().toISOString().slice(0,10),renewalCount:2,trialCount:0,goalCount:1,items:[],
       messageText:'Rezumat privat: 2 scadențe și 1 obiectiv.\n'+(preferences.includeNames?literal:'Numele nu sunt incluse.')+'\nhttps://expenses.micutu.com/#notifications'};
     }else throw Error('Unknown notification fixture endpoint '+p);
    }else if(p==='/api/account/mfa')data={enabled:false,available:true,recoveryCodesRemaining:0,pendingSetupId:null};
    else if(p==='/api/auth/me'){if(authed)data={username,accountContext:'A'.repeat(64)};else status=401;}
    else if(p==='/api/auth/refresh')status=401;
    else if(p==='/api/auth/logout'){authed=false;status=204;}
    else if(p==='/api/account')data={username,email,emailVerified:true};
    else if(p==='/api/account/finance-tokens')data={available:false,tokens:[]};
    else if(p==='/api/account/sessions')data=[];
    else if(p==='/api/subscriptions')data={items:[],total:0};
    else if(p==='/api/goals'||p==='/api/budgets')data=[];
    else if(p==='/api/subscriptions/summary')data={byCurrency:[],totalSubscriptions:0,activeSubscriptions:0,dueThisWeek:0};
    else if(p==='/api/subscriptions/insights')data={forecast:[],upcoming:[],overdue:[],byCategory:[],savingsOpportunities:[]};
    else if(p==='/api/subscriptions/duplicates')data={checkedCount:0,groups:[]};
    await route.fulfill({status,contentType:'application/json',body:status===204?'':JSON.stringify(data)});
   });
   await page.goto('http://127.0.0.1:'+server.address().port+'/#notifications');
   const dialog=page.getByRole('dialog'),panel=page.locator('section[aria-labelledby="notification-title"]');
   await panel.getByText('Preferințe temporar indisponibile.',{exact:true}).waitFor();checks++;
   await panel.getByRole('button',{name:'Reîncarcă preferințele',exact:true}).click();
   const enable=panel.getByRole('checkbox',{name:/Vreau rezumatul zilnic/});
   await enable.waitFor();assert.equal(await enable.isChecked(),false);checks++;
   await enable.check();await panel.getByLabel('Fus orar pentru notificări',{exact:true}).selectOption('America/New_York');
   await panel.getByLabel('Ora notificărilor',{exact:true}).selectOption('8');
   await panel.getByLabel('Fereastra notificărilor',{exact:true}).selectOption('3');
   await panel.getByLabel('Termene ale obiectivelor de economisire',{exact:true}).check();
   assert.equal(await panel.getByRole('button',{name:'Previzualizează rezumatul',exact:true}).isDisabled(),true);checks++;
   await panel.getByRole('button',{name:'Salvează notificările',exact:true}).click();
   await panel.getByText('Preferințe modificate. Reîncarcă-le.',{exact:true}).waitFor();
   assert.equal(await enable.isChecked(),true);assert.equal(await panel.getByLabel('Ora notificărilor',{exact:true}).inputValue(),'8');checks+=2;
   await panel.getByRole('button',{name:'Reîncarcă preferințele',exact:true}).click();
   await enable.waitFor();assert.equal(await enable.isChecked(),false);checks++;
   await enable.check();await panel.getByLabel('Fus orar pentru notificări',{exact:true}).selectOption('America/New_York');
   await panel.getByLabel('Ora notificărilor',{exact:true}).selectOption('8');
   await panel.getByLabel('Încheierea perioadelor de probă',{exact:true}).uncheck();
   await panel.getByLabel('Termene ale obiectivelor de economisire',{exact:true}).check();
   await panel.getByRole('button',{name:'Salvează notificările',exact:true}).click();
   await panel.getByText(/Următoarea verificare:/).waitFor();
   assert.equal(preferences.timeZone,'America/New_York');assert.equal(preferences.sendHour,8);assert.equal(preferences.includeNames,false);checks+=3;
   await panel.getByRole('button',{name:'Previzualizează rezumatul',exact:true}).click();
   await panel.getByText(/Numele nu sunt incluse/).waitFor();assert.equal(previewReads,1);checks++;
   await panel.getByLabel('Include numele în email (opțional)',{exact:true}).check();
   await panel.getByRole('button',{name:'Salvează notificările',exact:true}).click();
   await panel.getByRole('button',{name:'Previzualizează rezumatul',exact:true}).click();
   await panel.getByText(/onerror="window.__mailXss=true"/).waitFor();
   assert.equal(await page.evaluate(()=>window.__mailXss),undefined);checks++;
   assert.ok(await panel.getByText('2026-11-16 · Acceptare neconfirmată',{exact:true}).isVisible());checks++;
   await page.waitForFunction(()=>!document.querySelector('.fixed[aria-live="polite"]')?.textContent?.trim());
   const audit=await new AxeBuilder({page}).include('[role="dialog"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target),details:v.nodes.map(n=>n.failureSummary)})),[],'Notification axe at '+width+'/'+theme);checks++;
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);checks++;
   fs.mkdirSync(path.resolve(__dirname,'../.artifacts/screenshots'),{recursive:true});
   await dialog.screenshot({path:path.resolve(__dirname,'../.artifacts/screenshots/notifications-'+width+'-'+theme+'.png')});
   preferences.available=false;
   await panel.getByRole('button',{name:'Reîncarcă preferințele',exact:true}).click();
   await panel.getByText(/Trimiterea emailurilor este indisponibilă/).waitFor();
   assert.equal(await enable.isDisabled(),false);await enable.uncheck();
   await panel.getByRole('button',{name:'Salvează notificările',exact:true}).click();
   await panel.getByRole('button',{name:'Salvează notificările',exact:true}).waitFor({state:'visible'});
   await page.waitForFunction(()=>document.querySelector('form[aria-label="Preferințe notificări"] button[type="submit"]')?.disabled);
   assert.equal(preferences.enabled,false);assert.equal(await enable.isDisabled(),true);checks+=3;
   preferences.available=true;preferences.canEnable=false;
   await panel.getByRole('button',{name:'Reîncarcă preferințele',exact:true}).click();
   await panel.getByText(/Verifică adresa curentă de email înainte/).waitFor();assert.equal(await enable.isDisabled(),true);checks++;
   slow=true;
   await panel.getByRole('button',{name:'Previzualizează rezumatul',exact:true}).click();await entered;
   await page.keyboard.press('Escape');await dialog.waitFor({state:'detached'});
   assert.equal(await page.evaluate(()=>location.hash),'');checks++;
   await page.getByRole('button',{name:'Ieșire',exact:true}).click();
   await page.getByRole('button',{name:'Conectare',exact:true}).waitFor();
   releasePreview();await page.waitForTimeout(150);
   assert.equal(await page.getByText(/Rezumat privat:/).count(),0);
   assert.equal(await page.evaluate(()=>JSON.stringify(localStorage).includes('Rezumat privat')),false);
   assert.deepEqual(errors,[]);checks+=3;
   await context.close();
  }
  console.log('PASS: '+checks+' notification assertions: explicit consent, privacy, preview, conflicts, outage opt-out, deep links, late responses, mobile and WCAG');
 }finally{await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
