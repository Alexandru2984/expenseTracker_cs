'use strict';
// All accounts, passwords and codes below are synthetic. No production API calls.
const http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const deps=path.resolve(__dirname,'../expense-tracker-ui'),root=path.join(deps,'dist');
const {chromium}=require(require.resolve('playwright',{paths:[deps]}));
const AxeBuilder=require(require.resolve('@axe-core/playwright',{paths:[deps]})).default;
const defaults=require('./notification-fixture.cjs');
const secret='GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ',password='synthetic-mfa-password',contextId='A'.repeat(64);
const codes=Array.from({length:10},(_,i)=>'ABCDE-F0123-45678-'+String(i).padStart(5,'0'));
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
   const origin='http://127.0.0.1:'+server.address().port;
   const context=await browser.newContext({viewport:{width,height:950},serviceWorkers:'block',reducedMotion:'reduce',permissions:['clipboard-read','clipboard-write']});
   await context.addInitScript(theme=>localStorage.setItem('theme',theme),theme);
   const page=await context.newPage(),errors=[],external=[];page.on('pageerror',e=>errors.push(e.message));
   let authed=true,enabled=false,remaining=0,pending=null,canceled=0,expired=false,loginProofs=0,refreshes=0,slow=false,releaseSetup,enteredSetup;
   const gate=new Promise(resolve=>{releaseSetup=resolve;}),entered=new Promise(resolve=>{enteredSetup=resolve;});
   await context.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url());
    if(url.origin!==origin){external.push(url.origin);await route.abort();return;}
    const p=url.pathname;if(!p.startsWith('/api/')){await route.continue();return;}
    let data={},status=200;
    if(p==='/api/auth/me'){if(authed)data={username:'mfa-fixture',accountContext:contextId};else status=401;}
    else if(p==='/api/auth/refresh'){refreshes++;status=401;}
    else if(p==='/api/auth/logout'){authed=false;status=204;}
    else if(p==='/api/auth/login'){assert.equal(req.postDataJSON().password,password);status=202;data={requiresTwoFactor:true,challengeToken:'c'.repeat(43),expiresAt:new Date(Date.now()+300000).toISOString()};}
    else if(p==='/api/auth/mfa-cancel'){canceled++;status=204;}
    else if(p==='/api/auth/mfa-login'){
     loginProofs++;assert.equal(req.postDataJSON().challengeToken,'c'.repeat(43));
     if(expired){status=401;data={title:'Verificarea MFA a expirat.',code:'mfa_restart_required'};expired=false;}
     else if(!['123456',codes[0]].includes(req.postDataJSON().code)){status=401;data={title:'Cod MFA incorect sau deja folosit.'};}
     else{authed=true;data={username:'mfa-fixture',accountContext:contextId};}
    }else if(p.startsWith('/api/account/mfa')){
     assert.equal(req.headers()['x-account-context'],contextId);
     if(p==='/api/account/mfa')data={enabled,available:true,recoveryCodesRemaining:remaining,pendingSetupId:pending};
     else if(p.endsWith('/setup')&&req.method()==='POST'){
      if(req.postDataJSON().currentPassword!==password){status=400;data={title:'Parolă sau cod incorect.'};}
      else{pending='11111111-1111-1111-1111-111111111111';data={setupId:pending,secret,otpauthUri:'otpauth://totp/Expense%20Tracker:mfa-fixture?secret='+secret+'&issuer=Expense%20Tracker',expiresAt:new Date(Date.now()+600000).toISOString()};if(slow){enteredSetup();await gate;}}
     }else if(p.endsWith('/setup')&&req.method()==='DELETE'){pending=null;status=204;}
     else if(p.endsWith('/confirm')){
      assert.equal(req.postDataJSON().setupId,pending);
      if(req.postDataJSON().code!=='123456'){status=400;data={title:'Parolă sau cod incorect.'};}
      else{enabled=true;remaining=10;pending=null;data={recoveryCodes:codes};}
     }else if(p.endsWith('/recovery-codes')||p.endsWith('/disable')){
      const proof=req.postDataJSON();assert.equal(proof.currentPassword,password);assert.equal(proof.code,codes[0]);
      if(p.endsWith('/disable')){enabled=false;remaining=0;status=204;}else{remaining=10;data={recoveryCodes:codes.map(c=>'F'+c.slice(1))};}
     }else throw Error('Unknown MFA endpoint '+p);
    }else if(p==='/api/account')data={username:'mfa-fixture',email:'mfa@example.invalid',emailVerified:true};
    else if(p==='/api/account/finance-tokens')data={available:true,tokens:[{id:'finance-fixture',label:'Finance F#',createdAt:new Date().toISOString(),expiresAt:new Date(Date.now()+86400000).toISOString()}]};
    else if(p==='/api/account/sessions')data=[];
    else if(p.startsWith('/api/account/notifications'))data=defaults(p);
    else if(p==='/api/subscriptions')data={items:[],total:0};
    else if(p==='/api/goals'||p==='/api/budgets')data=[];
    else if(p==='/api/subscriptions/summary')data={byCurrency:[],totalSubscriptions:0,activeSubscriptions:0,dueThisWeek:0};
    else if(p==='/api/subscriptions/insights')data={forecast:[],upcoming:[],overdue:[],byCategory:[],savingsOpportunities:[]};
    else if(p==='/api/subscriptions/duplicates')data={checkedCount:0,groups:[]};
    await route.fulfill({status,contentType:'application/json',body:status===204?'':JSON.stringify(data)});
   });
   const panel=page.locator('section[aria-labelledby="mfa-title"]'),dialog=page.getByRole('dialog');
   await page.goto(origin+'/#security');await panel.getByText('MFA este oprită',{exact:true}).waitFor();
   assert.equal(await page.evaluate(()=>document.activeElement?.getAttribute('aria-labelledby')),'mfa-title');checks++;
   await panel.getByLabel('Parola pentru configurarea MFA',{exact:true}).fill('wrong');
   await panel.getByRole('button',{name:'Configurează autentificatorul',exact:true}).click();await panel.getByRole('alert').waitFor();checks++;
   await panel.getByLabel('Parola pentru configurarea MFA',{exact:true}).fill(password);
   await panel.getByRole('button',{name:'Configurează autentificatorul',exact:true}).click();
   const qr=panel.getByRole('img',{name:'Cod QR secret pentru configurarea autentificatorului'});await qr.waitFor();
   assert.match(await qr.getAttribute('src'),/^data:image\/png;base64,/);assert.equal(await panel.getByLabel('Cheie manuală MFA',{exact:true}).inputValue(),secret);checks+=2;
   const audit=async selector=>{
    // QR rendering precedes the final status request. Audit the enabled form,
    // after busy state has settled, rather than mixing two DOM states in axe.
    await page.waitForFunction(()=>{const button=document.querySelector('section[aria-labelledby="mfa-title"] button[type="submit"]');return !button||(!button.disabled&&getComputedStyle(button).opacity==='1');});
    await page.waitForFunction(()=>!document.querySelector('.fixed[aria-live="polite"]')?.textContent?.trim());
    const result=await new AxeBuilder({page}).include(selector).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
    assert.deepEqual(result.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target),details:v.nodes.map(n=>n.failureSummary)})),[],width+'/'+theme);checks++;
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);checks++;
   };
   await audit('[role="dialog"]');
   await panel.getByLabel('Codul de confirmare MFA',{exact:true}).fill('000000');
   await panel.getByRole('button',{name:'Activează MFA',exact:true}).click();await panel.getByRole('alert').waitFor();
   await panel.getByLabel('Codul de confirmare MFA',{exact:true}).fill('123456');
   await panel.getByRole('button',{name:'Activează MFA',exact:true}).click();await panel.getByText('MFA este activată',{exact:true}).waitFor();
   assert.equal(await panel.getByLabel('Coduri de recuperare MFA',{exact:true}).count(),0);
   assert.equal(await page.getByRole('list',{name:'Tokenuri Finance active'}).getByRole('listitem').count(),1);checks+=2;
   await panel.getByRole('button',{name:'Arată codurile de recuperare',exact:true}).click();
   assert.equal(await panel.getByLabel('Coduri de recuperare MFA',{exact:true}).inputValue(),codes.join('\n'));checks++;
   await panel.getByRole('button',{name:'Copiază codurile de recuperare',exact:true}).click();
   assert.equal(await page.evaluate(()=>navigator.clipboard.readText()),codes.join('\n'));checks++;
   assert.equal(await page.evaluate(({secret,code,password})=>[localStorage,sessionStorage].some(s=>[secret,code,password].some(v=>JSON.stringify(s).includes(v))),{secret,code:codes[0],password}),false);checks++;
   fs.mkdirSync(path.resolve(__dirname,'../.artifacts/screenshots'),{recursive:true});
   await panel.screenshot({path:path.resolve(__dirname,'../.artifacts/screenshots/mfa-'+width+'-'+theme+'.png')});
   await panel.getByRole('button',{name:'Am salvat codurile',exact:true}).click();
   await panel.getByRole('button',{name:'Înlocuiește codurile de recuperare',exact:true}).click();
   await panel.getByLabel('Parola pentru modificarea MFA',{exact:true}).fill(password);await panel.getByLabel('Cod MFA sau cod de recuperare',{exact:true}).fill(codes[0]);
   await panel.getByRole('button',{name:'Generează coduri noi',exact:true}).click();await panel.getByRole('button',{name:'Arată codurile de recuperare',exact:true}).click();
   assert.match(await panel.getByLabel('Coduri de recuperare MFA',{exact:true}).inputValue(),/^FBCDE/);checks++;
   await panel.getByRole('button',{name:'Am salvat codurile',exact:true}).click();
   await panel.getByRole('button',{name:'Dezactivează MFA',exact:true}).click();
   await panel.getByLabel('Parola pentru modificarea MFA',{exact:true}).fill(password);await panel.getByLabel('Cod MFA sau cod de recuperare',{exact:true}).fill(codes[0]);
   await panel.getByRole('button',{name:'Confirmă dezactivarea MFA',exact:true}).click();await panel.getByText('MFA este oprită',{exact:true}).waitFor();assert.equal(await page.getByRole('list',{name:'Tokenuri Finance active'}).getByRole('listitem').count(),1);checks+=2;
   // A response to setup that arrives after logout must not render its secret.
   slow=true;await panel.getByLabel('Parola pentru configurarea MFA',{exact:true}).fill(password);
   await panel.getByRole('button',{name:'Configurează autentificatorul',exact:true}).click();await entered;
   await page.keyboard.press('Escape');await dialog.waitFor({state:'detached'});
   await page.getByRole('button',{name:'Ieșire',exact:true}).click();await page.getByRole('button',{name:'Conectare',exact:true}).waitFor();releaseSetup();
   await page.waitForTimeout(100);assert.equal(await page.getByLabel('Cheie manuală MFA',{exact:true}).count(),0);checks++;
   const beginLogin=async()=>{
    await page.getByLabel('Username',{exact:true}).fill('mfa-fixture');await page.getByLabel('Parolă',{exact:true}).fill(password);
    await page.getByRole('button',{name:'Conectare',exact:true}).click();await page.getByLabel('Cod din autentificator',{exact:true}).waitFor();
    assert.equal(authed,false);assert.equal(await page.getByLabel('Parolă',{exact:true}).count(),0);checks+=2;
   };
   await beginLogin();await page.getByRole('button',{name:'Înapoi la conectare',exact:true}).click();
   await page.waitForFunction(()=>!!document.querySelector('#login-password'));await page.waitForTimeout(100);assert.equal(canceled,1);checks++;
   await beginLogin();expired=true;await page.getByLabel('Cod din autentificator',{exact:true}).fill('123456');
   await page.getByRole('button',{name:'Verifică MFA și intră',exact:true}).click();await page.getByLabel('Parolă',{exact:true}).waitFor();checks++;
   await beginLogin();const before=refreshes;
   await page.getByLabel('Cod din autentificator',{exact:true}).fill('000000');await page.getByRole('button',{name:'Verifică MFA și intră',exact:true}).click();
   await page.getByText('Cod MFA incorect sau deja folosit.',{exact:true}).waitFor();assert.equal(refreshes,before);checks++;
   await audit('body');
   await page.getByRole('button',{name:'Folosește un cod de recuperare',exact:true}).click();await page.getByLabel('Cod de recuperare',{exact:true}).fill(codes[0]);
   await page.getByRole('button',{name:'Verifică MFA și intră',exact:true}).click();await page.getByRole('button',{name:'Ieșire',exact:true}).waitFor();
   assert.equal(loginProofs,3);assert.equal(authed,true);assert.deepEqual(errors,[]);assert.ok(external.every(s=>s==='https://analytics.micutu.com'),'No external QR provider');checks+=4;
   await context.close();
  }
  console.log('PASS: '+checks+' MFA assertions: private QR, enrollment, recovery, login challenges, expiry, late responses, mobile and WCAG');
 }finally{await browser.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
