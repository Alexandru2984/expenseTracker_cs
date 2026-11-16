'use strict';
const http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const deps=path.resolve(__dirname,'../expense-tracker-ui'),root=path.join(deps,'dist');
const {chromium}=require(require.resolve('playwright',{paths:[deps]}));
const AxeBuilder=require(require.resolve('@axe-core/playwright',{paths:[deps]})).default;
const defaults=require('./notification-fixture.cjs');
const username='D'.repeat(50),password='synthetic-private-password',accountContext='A'.repeat(64);
const archive={format:'expense-tracker-account',schemaVersion:1,profile:{username},subscriptions:[{name:'private archive <img src=x onerror="window.__archiveXss=true">'}]};
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
   const page=await context.newPage(),errors=[],downloads=[];page.on('pageerror',e=>errors.push(e.message));page.on('download',d=>downloads.push(d));
   let authed=true,summaryFailure=1,exportLarge=1,deleteRequests=0,exportRequests=0,delayExport=false,exportCooldown=0;
   let releaseDelete,enteredDelete,releaseExport,enteredExport;
   const deleteGate=new Promise(r=>{releaseDelete=r;}),deleteEntered=new Promise(r=>{enteredDelete=r;});
   const exportGate=new Promise(r=>{releaseExport=r;}),exportEntered=new Promise(r=>{enteredExport=r;});
   await context.route('https://analytics.micutu.com/**',r=>r.abort());
   await context.route('**/api/**',async route=>{
    const req=route.request(),p=new URL(req.url()).pathname;let data={},status=200;
    if(p.startsWith('/api/account/data')){
     assert.equal(req.headers()['x-account-context'],accountContext);
     if(p==='/api/account/data'){
      if(!authed)status=401;
      else if(summaryFailure-->0){status=503;data={title:'Datele contului sunt temporar indisponibile.'};}
      else data={username,mfaEnabled:true,exportRetryAfterSeconds:exportCooldown,subscriptions:1500,budgets:2,goals:3,contributions:17,financeTokens:1};
     }else if(p.endsWith('/export')){
      exportRequests++;const proof=req.postDataJSON();assert.equal(proof.code,'123456');
      if(proof.currentPassword!==password){status=400;data={title:'Parolă sau cod incorect.'};}
      else if(exportLarge-->0){status=413;data={title:'Arhiva depășește 16 MiB. Nu s-a descărcat un fișier parțial.'};exportCooldown=1;}
      else{if(delayExport){enteredExport();await exportGate;}data=archive;exportCooldown=0;}
     }else if(p.endsWith('/delete')){
      deleteRequests++;const proof=req.postDataJSON();assert.equal(proof.currentPassword,password);assert.equal(proof.code,'123456');assert.equal(proof.confirmationUsername,username);
      if(deleteRequests===1){await route.abort('connectionfailed');return;}
      enteredDelete();await deleteGate;authed=false;data={deleted:true};
     }else throw Error('Unknown account data route '+p);
    }else if(p==='/api/auth/me'){if(authed)data={username,accountContext};else status=401;}
    else if(p==='/api/auth/refresh')status=401;
    else if(p==='/api/auth/logout'){authed=false;status=204;}
    else if(p==='/api/account')data={username,email:'private@example.invalid',emailVerified:true};
    else if(p==='/api/account/mfa')data={enabled:true,available:true,recoveryCodesRemaining:10};
    else if(p==='/api/account/finance-tokens')data={available:true,tokens:[]};
    else if(p==='/api/account/sessions')data=[];
    else if(p.startsWith('/api/account/notifications'))data=defaults(p);
    else if(p==='/api/subscriptions')data={items:[],total:0};
    else if(p==='/api/goals'||p==='/api/budgets')data=[];
    else if(p==='/api/subscriptions/summary')data={byCurrency:[],totalSubscriptions:0,activeSubscriptions:0,dueThisWeek:0};
    else if(p==='/api/subscriptions/insights')data={forecast:[],upcoming:[],overdue:[],byCategory:[],savingsOpportunities:[]};
    else if(p==='/api/subscriptions/duplicates')data={checkedCount:0,groups:[]};
    await route.fulfill({status,contentType:'application/json',body:status===204?'':JSON.stringify(data)});
   });
   const origin='http://127.0.0.1:'+server.address().port,dialog=page.getByRole('dialog'),panel=page.locator('section[aria-labelledby="account-data-title"]');
   await page.goto(origin+'/#data');await panel.getByText('Datele contului sunt temporar indisponibile.',{exact:true}).waitFor();checks++;
   await panel.getByRole('button',{name:'Reîncarcă datele contului',exact:true}).click();await panel.getByText('1500',{exact:true}).waitFor();checks++;
   const proof=async(pw=password)=>{
    await panel.getByLabel('Parola pentru datele contului',{exact:true}).fill(pw);await panel.getByLabel('Cod MFA pentru datele contului',{exact:true}).fill('123456');
   };
   await panel.getByRole('button',{name:'Descarcă arhiva JSON',exact:true}).click();await proof('wrong');
   await panel.getByRole('button',{name:'Descarcă datele confirmate',exact:true}).click();await panel.getByText('Parolă sau cod incorect.',{exact:true}).waitFor();
   assert.equal(await panel.getByLabel('Cod MFA pentru datele contului',{exact:true}).inputValue(),'');assert.equal(downloads.length,0);checks+=2;
   await proof();await panel.getByRole('button',{name:'Descarcă datele confirmate',exact:true}).click();
   await panel.getByText('Arhiva depășește 16 MiB. Nu s-a descărcat un fișier parțial.',{exact:true}).waitFor();assert.equal(downloads.length,0);checks++;
   await page.waitForFunction(()=>[...document.querySelectorAll('section[aria-labelledby="account-data-title"] button')].some(b=>b.textContent.trim()==='Descarcă datele confirmate'&&!b.disabled));
   exportCooldown=0;await proof();const downloaded=page.waitForEvent('download');
   await panel.getByRole('button',{name:'Descarcă datele confirmate',exact:true}).click();const file=await downloaded;
   assert.match(file.suggestedFilename(),/^expense-tracker-account-\d{4}-\d{2}-\d{2}\.json$/);
   assert.deepEqual(JSON.parse(fs.readFileSync(await file.path(),'utf8')),archive);assert.equal(await page.evaluate(()=>window.__archiveXss),undefined);checks+=3;
   await panel.getByRole('button',{name:'Șterge contul meu',exact:true}).click();const destroy=panel.getByRole('button',{name:'Șterge definitiv contul',exact:true});
   assert.equal(await destroy.isDisabled(),true);checks++;
   await panel.getByLabel('Numele contului pentru ștergere',{exact:true}).fill('wrong');await proof();
   await panel.getByRole('checkbox',{name:/Înțeleg că ștergerea/}).check();assert.equal(await destroy.isDisabled(),true);checks++;
   await panel.getByLabel('Numele contului pentru ștergere',{exact:true}).fill(username);assert.equal(await destroy.isDisabled(),false);checks++;
   assert.ok(await panel.getByText(/Datele deja importate în Finance F#/).isVisible());assert.ok(await panel.getByText(/Backupurile și logurile existente/).isVisible());checks+=2;
   await page.waitForFunction(()=>!document.querySelector('.fixed[aria-live="polite"]')?.textContent?.trim());
   const audit=await new AxeBuilder({page}).include('[role="dialog"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target),details:v.nodes.map(n=>n.failureSummary)})),[],width+'/'+theme);checks++;
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);checks++;
   fs.mkdirSync(path.resolve(__dirname,'../.artifacts/screenshots'),{recursive:true});
   await panel.screenshot({path:path.resolve(__dirname,'../.artifacts/screenshots/account-data-'+width+'-'+theme+'.png')});
   await destroy.click();await panel.getByRole('alert').waitFor();await page.waitForTimeout(100);
   assert.equal(deleteRequests,1);assert.equal(authed,true);checks+=2;
   await panel.getByRole('button',{name:'Reîncarcă datele contului',exact:true}).click();await proof();
   await destroy.click();await deleteEntered;assert.equal(await panel.locator('form button[type="submit"]').isDisabled(),true);checks++;
   // Closing the dialog does not cancel a deletion already submitted to the server.
   await page.keyboard.press('Escape');await dialog.waitFor({state:'detached'});releaseDelete();
   await page.getByRole('button',{name:'Conectare',exact:true}).waitFor();
   assert.equal(await page.evaluate(()=>location.hash),'');assert.equal(deleteRequests,2);checks+=2;
   // A late export must not download after logout, including when the panel is gone.
   authed=true;summaryFailure=0;delayExport=true;exportCooldown=0;
   await page.goto(origin+'/?scenario=late#data');await panel.getByRole('button',{name:'Descarcă arhiva JSON',exact:true}).click();await proof();
   await panel.getByRole('button',{name:'Descarcă datele confirmate',exact:true}).click();await exportEntered;
   await page.keyboard.press('Escape');await dialog.waitFor({state:'detached'});
   await page.getByRole('button',{name:'Ieșire',exact:true}).click();await page.getByRole('button',{name:'Conectare',exact:true}).waitFor();releaseExport();
   await page.waitForTimeout(150);assert.equal(downloads.length,1);
   assert.equal(await page.evaluate(pw=>[localStorage,sessionStorage].some(s=>JSON.stringify(s).includes(pw)||JSON.stringify(s).includes('private archive')),password),false);
   assert.deepEqual(errors,[]);assert.equal(exportRequests,4);checks+=4;
   await context.close();
  }
  console.log('PASS: '+checks+' account data assertions: scoped JSON download, proof, byte limit, deliberate deletion, lost responses, closed dialog, mobile and WCAG');
 }finally{await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
