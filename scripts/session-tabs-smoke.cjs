'use strict';
// Isolated synthetic API, real built SPA and real API/coordinator modules.
// Source modules are served only by this test server; no test hook ships in dist.
const http = require('node:http'), fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const deps = path.resolve(__dirname, '../expense-tracker-ui');
const { chromium } = require(require.resolve('playwright', { paths: [deps] }));
const key = 'expense.auth.boundary.v1', binding = actor => actor.repeat(64);
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
function fixture() { return { actor: 'A', expired: false, writes: [], events: [], refreshes: 0, blocked: 0 }; }
let state = fixture();
const server = http.createServer(async (request, response) => {
 try {
  const url = new URL(request.url, 'http://localhost'), pathname = url.pathname;
  if (pathname.startsWith('/api/')) {
   let body = ''; for await (const chunk of request) body += chunk;
   const payload = body ? JSON.parse(body) : {};
   let data = {}, status = 200;
   const context = request.headers['x-account-context'];
   const changed = context && state.actor && context !== binding(state.actor);
   const json = () => { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(status === 204 ? '' : JSON.stringify(data)); };
   if (pathname === '/api/auth/me') {
    if (!state.actor || state.expired) status = 401;
    else data = { username: state.actor, accountContext: binding(state.actor) };
   } else if (changed) {
    state.blocked++; status = 409; data = { code: 'account_context_changed' };
   } else if (pathname === '/api/auth/login') {
    state.events.push('login'); state.actor = payload.username; state.expired = false;
    data = { username: state.actor, accountContext: binding(state.actor) };
   } else if (pathname === '/api/auth/refresh') {
    state.events.push('refresh:start'); state.refreshes++;
    state.refreshStarted?.resolve(); if (state.refreshGate) await state.refreshGate.promise;
    if (!state.actor) status = 401;
    else { state.expired = false; data = { username: state.actor, accountContext: binding(state.actor) }; }
    state.events.push('refresh:end');
   } else if (pathname === '/api/auth/logout') { state.actor = null; status = 204; }
   else if (!state.actor || state.expired) status = 401;
   else if (pathname === '/api/account/change-password') { state.events.push('password'); status = 204; }
   else if (pathname === '/api/subscriptions' && request.method === 'POST') {
    state.writes.push({ actor: state.actor, payload }); state.writeStarted?.resolve();
    if (state.writeGate) await state.writeGate.promise;
    status = 201; data = { id: 'synthetic', ...payload };
   } else if (pathname === '/api/subscriptions') data = { items: [], total: 0 };
   else if (pathname === '/api/subscriptions/summary') {
    if (state.readGate) { state.readStarted.resolve(); await state.readGate.promise; }
    data = { byCurrency: [], activeSubscriptions: 0, totalSubscriptions: 0, dueThisWeek: 0 };
   } else if (pathname === '/api/subscriptions/insights') data = { forecast: [], byCategory: [], upcoming: [], overdue: [], savingsOpportunities: [] };
   else if (pathname === '/api/subscriptions/duplicates') data = { checkedCount: 0, groups: [] };
   else if (pathname === '/api/budgets' || pathname === '/api/goals') data = [];
   else if (pathname === '/api/account') data = { username: state.actor, email: state.actor + '@example.invalid', emailVerified: true };
   else if (pathname === '/api/subscriptions/rates/status') data = { rates: { RON: 1 }, source: 'fallback' };
   else if (pathname === '/api/subscriptions/export') { response.writeHead(200, { 'Content-Type': 'text/csv' }); response.end('synthetic'); return; }
   else throw new Error('Unhandled fixture endpoint ' + request.method + ' ' + pathname);
   json(); return;
  }
  if (pathname === '/__harness') {
   response.setHeader('Content-Type', 'text/html');
   response.end('<!doctype html><script type="module">import * as client from "/__source/api.js"; window.client=client; window.ready=(async()=>{try{const r=await client.authApi.me();client.advanceApiSession(r.data.accountContext)}catch(e){if(e.response?.status!==401)throw e}})();</script>'); return;
  }
  let file;
  if (pathname === '/__source/api.js' || pathname === '/__source/sessionCoordinator.js') {
   file = path.join(deps, 'src', path.basename(pathname));
   const content = fs.readFileSync(file, 'utf8').replace("from 'axios'", "from '/__axios.js'").replace('import.meta.env.VITE_API_URL', 'undefined');
   response.setHeader('Content-Type', 'application/javascript'); response.end(content); return;
  }
  file = pathname === '/__axios.js' ? path.join(deps, 'node_modules/axios/dist/esm/axios.js')
   : path.resolve(deps, 'dist', '.' + (pathname === '/' ? '/index.html' : pathname));
  if (pathname !== '/__axios.js' && !file.startsWith(path.join(deps, 'dist') + path.sep)) { response.writeHead(404).end(); return; }
  response.setHeader('Content-Type', file.endsWith('.js') ? 'application/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.svg') ? 'image/svg+xml' : 'text/html');
  response.end(fs.readFileSync(file));
 } catch (error) { response.writeHead(500).end(); console.error(error); }
});
(async () => {
 await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
 const origin = 'http://127.0.0.1:' + server.address().port, browser = await chromium.launch({ headless: true });
 let checks = 0;
 const errors = [];
 async function context(mode = 'normal') {
  const ctx = await browser.newContext({ viewport: { width: 390, height: 900 }, serviceWorkers: 'block' });
  ctx.on('page', page => page.on('pageerror', error => errors.push(error.message)));
  await ctx.route('https://analytics.micutu.com/**', route => route.abort());
  await ctx.addInitScript(({ mode, key }) => {
   if (['storage', 'isolated', 'resume'].includes(mode)) window.BroadcastChannel = undefined;
   if (['channel', 'isolated'].includes(mode)) {
    const get = Storage.prototype.getItem, set = Storage.prototype.setItem;
    Storage.prototype.getItem = function(k) { if (k === key) throw new Error('Storage disabled by fixture'); return get.call(this, k); };
    Storage.prototype.setItem = function(k, value) { if (k === key) throw new Error('Storage disabled by fixture'); return set.call(this, k, value); };
   }
   if (mode === 'isolated') Object.defineProperty(navigator, 'locks', { value: undefined });
   if (mode === 'resume') window.addEventListener('storage', e => e.stopImmediatePropagation());
  }, { mode, key });
  return ctx;
 }
 async function harness(ctx) { const page = await ctx.newPage(); await page.goto(origin + '/__harness'); await page.evaluate(() => window.ready); return page; }
 async function run(page, source) { return page.evaluate(async source => { try { await new Function('return ' + source)(); return 'ok'; } catch(e) { return e.code ?? e.message; } }, source); }
 const heading = (page, actor) => page.getByRole('heading', { name: 'Bună, ' + actor + '.', exact: true }).waitFor();
 try {
  for (const mode of ['normal', 'storage', 'channel']) {
   state = fixture(); const ctx = await context(mode);
   const tab = await ctx.newPage(), other = await ctx.newPage();
   await Promise.all([tab.goto(origin), other.goto(origin)]);
   await Promise.all([heading(tab, 'A'), heading(other, 'A')]);
   await tab.getByRole('button', { name: '+ Nou', exact: true }).click();
   await tab.getByLabel('Nume', { exact: true }).fill('Private draft A');
   const writer = await harness(ctx);
   assert.equal(await run(writer, 'window.client.authApi.logout()'), 'ok');
   await Promise.all([tab.getByRole('button', { name: 'Conectare', exact: true }).waitFor(), other.getByRole('button', { name: 'Conectare', exact: true }).waitFor()]);
   assert.equal(await run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})"), 'ok');
   await Promise.all([heading(tab, 'B'), heading(other, 'B')]);
   assert.equal(await tab.getByLabel('Nume', { exact: true }).count(), 0);
   assert.equal(state.writes.length, 0); checks += 4;
   if (mode !== 'channel') {
    const marker = await writer.evaluate(key => JSON.parse(localStorage.getItem(key)), key);
    assert.deepEqual(Object.keys(marker).sort(), ['id', 'phase']); assert.equal(marker.phase, 'ready'); checks += 2;
   }
   await ctx.close();
  }
  // A missed storage event is detected before sending the old action.
  state = fixture();
  {
   const ctx = await context('resume'), reader = await harness(ctx), writer = await harness(ctx);
   await run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})");
   assert.equal(await run(reader, "window.client.subscriptionsApi.create({name:'stale'})"), 'ERR_CANCELED');
   assert.equal(state.writes.length, 0); checks += 2; await ctx.close();
  }
  // Even with all cross-tab mechanisms unavailable, the server binding refuses
  // wrong-account mutations, logout, refresh and blob downloads without replay.
  for (const action of ["goalsApi.create({name:'stale'})", "subscriptionsApi.create({name:'stale'})", 'authApi.logout()', 'subscriptionsApi.exportCsv()', "accountDataApi.export({currentPassword:'stale'})"]) {
   state = fixture(); const ctx = await context('isolated'), reader = await harness(ctx), writer = await harness(ctx);
   assert.equal(await run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})"), 'ok');
   assert.equal(await run(reader, 'window.client.' + action), 'ERR_CANCELED');
   assert.equal(state.actor, 'B'); assert.equal(state.blocked, 1); assert.equal(state.writes.length, 0); checks += 5;
   await ctx.close();
  }
  // Account deletion checks the active actor before submitting the destructive POST.
  state = fixture();
  {
   const ctx = await context('isolated'), reader = await harness(ctx), writer = await harness(ctx);
   assert.equal(await run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})"), 'ok');
   assert.equal(await run(reader, "window.client.accountDataApi.remove({currentPassword:'stale',confirmationUsername:'stale'})"), 'ERR_CANCELED');
   assert.equal(state.actor, 'B'); assert.equal(state.blocked, 0); assert.equal(state.writes.length, 0); checks += 5;
   await ctx.close();
  }
  // Login waits for an in-flight refresh cookie write, then becomes final actor.
  state = fixture();
  {
   const ctx = await context(), refresher = await harness(ctx), writer = await harness(ctx);
   state.expired = true; state.refreshGate = deferred(); state.refreshStarted = deferred();
   const refresh = run(refresher, 'window.client.subscriptionsApi.getAll()');
   await state.refreshStarted.promise;
   const login = run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})");
   await writer.waitForTimeout(150); assert.deepEqual(state.events, ['refresh:start']); checks++;
   state.refreshGate.resolve();
   assert.equal(await login, 'ok'); await refresh;
   assert.deepEqual(state.events, ['refresh:start', 'refresh:end', 'login']); assert.equal(state.actor, 'B'); checks += 3;
   await ctx.close();
  }
  // Protected cookie mutations repair an expired session inside the same lock,
  // without nesting a second exclusive refresh lock.
  state = fixture();
  {
   const ctx = await context(), page = await harness(ctx); state.expired = true;
   assert.equal(await run(page, "window.client.accountApi.changePassword({currentPassword:'synthetic',newPassword:'synthetic-new'})"), 'ok');
   assert.deepEqual(state.events, ['refresh:start', 'refresh:end', 'password']); assert.equal(state.refreshes, 1); checks += 3;
   await ctx.close();
  }
  // A revoked refresh token exits protected mutation preflight cleanly.
  state = fixture();
  {
   const ctx = await context(), page = await harness(ctx);
   await page.evaluate(() => { window.logouts = 0; window.addEventListener('auth:logout', () => window.logouts++); });
   state.actor = null;
   assert.equal(await run(page, "window.client.accountApi.changePassword({currentPassword:'synthetic',newPassword:'synthetic-new'})"), 'ERR_BAD_REQUEST');
   assert.equal(await page.evaluate(() => window.logouts), 1);
   assert.equal(state.events.includes('password'), false); checks += 3;
   await ctx.close();
  }
  // A queued old-account logout is cancelled after another writer changes actor.
  state = fixture();
  {
   const ctx = await context(), old = await harness(ctx), writer = await harness(ctx);
   await writer.evaluate(() => {
    window.lockEntered = new Promise(resolve => { window.markEntered = resolve; });
    window.releaseGate = new Promise(resolve => { window.releaseLock = resolve; });
    navigator.locks.request('expense-session-refresh', async () => { window.markEntered(); await window.releaseGate; });
   });
   await writer.evaluate(() => window.lockEntered);
   const login = run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})");
   await writer.waitForTimeout(50);
   const logout = run(old, 'window.client.authApi.logout()');
   await writer.evaluate(() => window.releaseLock());
   assert.equal(await login, 'ok'); assert.equal(await logout, 'ERR_CANCELED'); assert.equal(state.actor, 'B'); checks += 3;
   await ctx.close();
  }
  // Closing a writer releases its Web Lock; discovery repairs its changing marker.
  state = fixture();
  {
   const ctx = await context(), abandoned = await harness(ctx);
   await abandoned.evaluate(async key => {
    localStorage.setItem(key, JSON.stringify({ id: crypto.randomUUID(), phase: 'changing' }));
    window.holdStarted = new Promise(resolve => { window.markHold = resolve; });
    navigator.locks.request('expense-session-refresh', async () => { window.markHold(); await new Promise(() => {}); });
    await window.holdStarted;
   }, key);
   await abandoned.close(); const recovered = await harness(ctx);
   assert.equal(await recovered.evaluate(key => JSON.parse(localStorage.getItem(key)).phase, key), 'ready');
   assert.equal(await run(recovered, 'window.client.subscriptionsApi.getAll()'), 'ok'); checks += 2;
   await ctx.close();
  }
  // An accepted old mutation may finish, but its late response must not close a
  // new account's form, show success or leave its save button busy.
  state = fixture();
  {
   const ctx = await context(), tab = await ctx.newPage(); await tab.goto(origin); await heading(tab, 'A');
   await tab.getByRole('button', { name: '+ Nou', exact: true }).click();
   await tab.getByLabel('Nume', { exact: true }).fill('Old accepted draft');
   await tab.getByLabel('Cost', { exact: true }).fill('10');
   state.writeGate = deferred(); state.writeStarted = deferred();
   await tab.getByRole('button', { name: 'Adaugă abonament', exact: true }).click(); await state.writeStarted.promise;
   const writer = await harness(ctx);
   await run(writer, "window.client.authApi.login({username:'B',password:'synthetic-password'})");
   await heading(tab, 'B');
   await tab.getByRole('button', { name: '+ Nou', exact: true }).click();
   await tab.getByLabel('Nume', { exact: true }).fill('New private draft');
   state.writeGate.resolve(); await tab.waitForTimeout(250);
   assert.equal(await tab.getByLabel('Nume', { exact: true }).inputValue(), 'New private draft');
   assert.equal(await tab.getByRole('button', { name: 'Adaugă abonament', exact: true }).isEnabled(), true);
   assert.equal(await tab.getByText('Abonament adăugat.', { exact: true }).count(), 0);
   assert.equal(state.writes[0].actor, 'A'); checks += 4;
   await ctx.close();
  }
  assert.deepEqual(errors, []); checks++;
  console.log('PASS: ' + checks + ' cross-tab assertions: identity binding, channel/storage fallbacks, cookie ordering, expired sessions, abandoned locks and stale UI');
 } finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
