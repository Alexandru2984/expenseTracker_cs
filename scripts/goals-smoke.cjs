'use strict';
const http = require('node:http'), fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const deps = path.resolve(__dirname, '../expense-tracker-ui'), root = path.join(deps, 'dist');
const { chromium } = require(require.resolve('playwright', { paths: [deps] }));
const AxeBuilder = require(require.resolve('@axe-core/playwright', { paths: [deps] })).default;
const today = () => new Date().toISOString().slice(0, 10);
const name = 'Fond pentru vacanță', literal = '<img src=x onerror="window.__goalXss=true">';
const screenshots = path.resolve(__dirname, '../.artifacts/screenshots');
fs.mkdirSync(screenshots, { recursive: true });
(async () => {
 const server = http.createServer((request, response) => {
  const pathname = new URL(request.url, 'http://localhost').pathname;
  const file = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
  if (!file.startsWith(root + path.sep)) { response.writeHead(404).end(); return; }
  fs.readFile(file, (error, data) => {
   if (error) { response.writeHead(404).end(); return; }
   response.setHeader('Content-Type', file.endsWith('.js') ? 'application/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.svg') ? 'image/svg+xml' : 'text/html'); response.end(data);
  });
 });
 await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
 const browser = await chromium.launch({ headless: true }); let checks = 0;
 try {
  for (const width of (process.env.EXPENSE_UI_WIDTHS ?? '1440,390,320').split(',').map(Number)) for (const theme of ['light', 'dark']) {
   const context = await browser.newContext({ viewport: { width, height: 950 }, serviceWorkers: 'block', reducedMotion: 'reduce' });
   await context.addInitScript(theme => localStorage.setItem('theme', theme), theme);
   const page = await context.newPage(), errors = []; page.on('pageerror', e => errors.push(e.message));
   let goal = null, entries = [], revision = 0, createFails = 1, listFails = 1, lostResponse = 1, deleteConflict = 1, editConflict = 1;
   const keys = [], requests = [];
   const version = () => '11111111-1111-1111-1111-' + String(++revision).padStart(12, '0');
   const dto = () => {
    const savedAmount = entries.reduce((sum, e) => sum + e.amount, 0), remaining = Math.max(0, goal.targetAmount - savedAmount);
    return { ...goal, savedAmount, remaining, contributionCount: entries.length, progressPercent: Math.min(100, savedAmount / goal.targetAmount * 100),
     status: goal.isArchived ? 'archived' : !remaining ? 'completed' : goal.targetDate === today() ? 'due-soon' : 'on-track', monthsToGoal: Math.ceil(remaining / goal.monthlyContribution),
     estimatedCompletionDate: '2027-12-31', requiredMonthlyContribution: goal.targetDate === today() ? null : 5, asOfDate: today() };
   };
   await context.route('https://analytics.micutu.com/**', route => route.abort());
   await context.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()), p = url.pathname, method = request.method();
    let data = {}, status = 200;
    if (p.startsWith('/api/goals')) {
     assert.equal(request.headers()['x-account-context'], 'A'.repeat(64), 'Every goal request binds the intended account');
     requests.push(method + ' ' + p);
     const payload = ['POST','PUT'].includes(method) ? request.postDataJSON() : null;
     if (p === '/api/goals' && method === 'GET') {
      if (listFails-- > 0) { status = 503; data = { title: 'Obiective temporar indisponibile.' }; }
      else data = goal ? [dto()] : [];
     } else if (p === '/api/goals' && method === 'POST') {
      if (createFails-- > 0) { status = 503; data = { title: 'Salvarea temporar indisponibilă.' }; }
      else { goal = { ...payload, id: 'goal-fixture', version: version(), createdAt: new Date().toISOString() }; data = goal; status = 201; }
     } else if (p.endsWith('/contributions') && method === 'POST') {
      keys.push(payload.id);
      const existing = entries.find(e => e.id === payload.id);
      if (existing) data = { id: existing.id, version: goal.version };
      else if (payload.version !== goal.version) { status = 409; data = { title: 'Obiectiv modificat. Reîncarcă obiectivele.' }; }
      else {
       entries.unshift({ ...payload, createdAt: new Date().toISOString() }); goal.version = version();
       if (lostResponse-- > 0) { status = 503; data = { title: 'Răspuns pierdut. Poți retrimite contribuția.' }; }
       else { status = 201; data = { id: payload.id, version: goal.version }; }
      }
     } else if (p.endsWith('/contributions') && method === 'GET') {
      const skip = Number(url.searchParams.get('skip')); data = { total: entries.length, skip, take: 20, items: entries.slice(skip, skip + 20) };
     } else if (p.includes('/contributions/') && method === 'DELETE') {
      if (deleteConflict-- > 0) { goal.version = version(); status = 409; data = { title: 'Contribuții modificate. Reîncarcă obiectivele.' }; }
      else { assert.equal(url.searchParams.get('version'), goal.version); entries = entries.filter(e => e.id !== p.split('/').at(-1)); goal.version = version(); status = 204; }
     } else if (method === 'PUT') {
      if (editConflict > 0 && payload.name === 'Nume din formularul vechi') { editConflict--; goal.version = version(); status = 409; data = { title: 'Obiectiv modificat. Reîncarcă obiectivele.' }; }
      else { assert.equal(payload.version, goal.version); goal = { ...goal, ...payload, version: version() }; data = goal; }
     } else if (method === 'DELETE') { assert.equal(url.searchParams.get('version'), goal.version); goal = null; entries = []; status = 204; }
     else throw Error('Unhandled goal fixture request');
    } else if (p === '/api/auth/me') data = { username: 'goals-fixture', accountContext: 'A'.repeat(64) };
    else if (p === '/api/subscriptions') data = { items: [], total: 0 };
    else if (p === '/api/budgets') data = [];
    else if (p === '/api/account') data = { username: 'goals-fixture', email: 'synthetic@example.invalid', emailVerified: true };
    else if (p === '/api/subscriptions/summary') data = { byCurrency: [], totalSubscriptions: 0, activeSubscriptions: 0, dueThisWeek: 0 };
    else if (p === '/api/subscriptions/duplicates') data = { checkedCount: 0, groups: [] };
    else if (p === '/api/subscriptions/rates/status') data = { rates: { RON: 1, EUR: .2 }, source: 'fallback' };
    else if (p === '/api/subscriptions/insights') data = { forecast: [], byCategory: [], upcoming: [], overdue: [], savingsOpportunities: [{ id: 'saving-fixture', name: 'Economii din streaming', currency: 'EUR', monthlyTotal: 10 }] };
    await route.fulfill({ status, contentType: 'application/json', body: status === 204 ? '' : JSON.stringify(data) });
   });
   const panel = page.locator('#goals-panel');
   await page.goto('http://127.0.0.1:' + server.address().port);
   await panel.getByRole('alert').filter({ hasText: 'Obiective temporar indisponibile.' }).waitFor();
   await panel.getByRole('button', { name: 'Reîncarcă obiectivele', exact: true }).click();
   await panel.getByText(/Primul pas: alege o țintă/).waitFor(); checks++;
   await page.getByLabel(/Economii din streaming/).check();
   await page.getByRole('button', { name: 'Creează un obiectiv cu economiile în EUR', exact: true }).click();
   await panel.getByLabel('Numele obiectivului', { exact: true }).waitFor();
   assert.equal(await panel.getByLabel('Valuta obiectivului', { exact: true }).inputValue(), 'EUR');
   assert.equal(await panel.getByLabel('Plan lunar', { exact: true }).inputValue(), '10'); checks += 2;
   await panel.getByLabel('Numele obiectivului', { exact: true }).fill(name);
   await panel.getByLabel('Suma țintă', { exact: true }).fill('100');
   await panel.getByLabel('Termen dorit', { exact: true }).fill(today());
   await panel.getByLabel('Descriere opțională', { exact: true }).fill(literal);
   await panel.getByRole('button', { name: 'Creează obiectivul', exact: true }).click();
   await panel.getByText('Salvarea temporar indisponibilă.', { exact: true }).waitFor();
   assert.equal(await panel.getByLabel('Numele obiectivului', { exact: true }).inputValue(), name); checks++;
   await panel.getByRole('button', { name: 'Creează obiectivul', exact: true }).click();
   await panel.getByRole('heading', { name, exact: true }).waitFor();
   await panel.getByText(/Proiecția lunară începe peste o lună/).waitFor(); checks++;
   assert.equal(await panel.getByText(literal, { exact: true }).count(), 1);
   assert.equal(await page.evaluate(() => window.__goalXss), undefined); checks += 2;
   await page.reload(); await panel.getByRole('heading', { name, exact: true }).waitFor(); checks++;
   await panel.getByRole('button', { name: 'Contribuie la ' + name, exact: true }).click();
   await panel.getByLabel('Suma economisită', { exact: true }).fill('25');
   await panel.getByLabel('Notă opțională', { exact: true }).fill(literal);
   await panel.getByRole('button', { name: 'Înregistrează contribuția', exact: true }).click();
   await panel.getByText('Răspuns pierdut. Poți retrimite contribuția.', { exact: true }).waitFor();
   assert.equal(await panel.getByLabel('Suma economisită', { exact: true }).inputValue(), '25'); checks++;
   await panel.getByRole('button', { name: 'Înregistrează contribuția', exact: true }).click();
   await page.getByText('Contribuție înregistrată.', { exact: true }).waitFor();
   assert.equal(keys.length, 2); assert.equal(keys[0], keys[1]); assert.equal(entries.length, 1);
   assert.equal(await panel.getByRole('progressbar').getAttribute('aria-valuenow'), '25'); checks += 4;
   // Another session has added history; it appears after explicit refresh.
   for (let i=0;i<24;i++) entries.push({ id: 'history-'+i, amount: 1, contributedOn: today(), note: i ? '' : 'x'.repeat(200), createdAt: new Date().toISOString() });
   goal.version = version();
   await panel.getByRole('button', { name: 'Reîncarcă obiectivele', exact: true }).click();
   await panel.getByRole('button', { name: 'Istoric ' + name, exact: true }).click();
   await panel.getByText('1–20 din 25', { exact: true }).waitFor(); checks++;
   await panel.getByRole('button', { name: 'Contribuții mai vechi', exact: true }).click();
   await panel.getByText('21–25 din 25', { exact: true }).waitFor(); checks++;
   await panel.getByRole('button', { name: 'Contribuții mai noi', exact: true }).click();
   await panel.getByText('1–20 din 25', { exact: true }).waitFor();
   await panel.getByRole('button', { name: /^Elimină contribuția/ }).first().click();
   await page.getByRole('button', { name: 'Elimină contribuția', exact: true }).click();
   await panel.getByText('Contribuții modificate. Reîncarcă obiectivele.', { exact: true }).waitFor();
   assert.equal(entries.length, 25); checks++;
   await panel.getByRole('button', { name: 'Reîncarcă obiectivele', exact: true }).click();
   await panel.getByRole('button', { name: 'Istoric ' + name, exact: true }).click();
   await panel.getByText('1–20 din 25', { exact: true }).waitFor();
   await panel.getByRole('button', { name: /^Elimină contribuția/ }).first().click();
   await page.getByRole('button', { name: 'Elimină contribuția', exact: true }).click();
   await page.getByText('Contribuție eliminată.', { exact: true }).waitFor();
   assert.equal(entries.length, 24); checks++;
   const audit = await new AxeBuilder({ page }).include('#goals-panel').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)})), []); checks++;
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth), false); checks++;
   assert.equal(await panel.getByRole('list', { name: 'Istoricul contribuțiilor' }).evaluate(el => el.scrollHeight > el.clientHeight), true); checks++;
   await panel.screenshot({ path: path.join(screenshots, 'goals-' + width + '-' + theme + '.png'), style: 'header, [aria-live="polite"] { visibility: hidden !important; }' });
   await panel.getByRole('button', { name: 'Editează obiectiv ' + name, exact: true }).click();
   assert.equal(await panel.getByLabel('Valuta obiectivului', { exact: true }).isDisabled(), true); checks++;
   await panel.getByLabel('Numele obiectivului', { exact: true }).fill('Nume din formularul vechi');
   await panel.getByRole('button', { name: 'Salvează obiectivul', exact: true }).click();
   await panel.getByText('Obiectiv modificat. Reîncarcă obiectivele.', { exact: true }).waitFor();
   assert.equal(await panel.getByLabel('Numele obiectivului', { exact: true }).inputValue(), 'Nume din formularul vechi'); checks++;
   await panel.getByRole('button', { name: 'Reîncarcă obiectivele', exact: true }).click();
   await panel.getByRole('button', { name: 'Editează obiectiv ' + name, exact: true }).click();
   await panel.getByLabel('Suma țintă', { exact: true }).fill('50');
   await panel.getByRole('button', { name: 'Salvează obiectivul', exact: true }).click();
   await page.getByText('Obiectiv salvat.', { exact: true }).waitFor();
   await panel.getByRole('button', { name: 'Contribuie la ' + name, exact: true }).click();
   await panel.getByLabel('Suma economisită', { exact: true }).fill('26');
   await panel.getByRole('button', { name: 'Înregistrează contribuția', exact: true }).click();
   await panel.getByText('Obiectiv atins', { exact: false }).waitFor();
   assert.equal(await panel.getByRole('button', { name: 'Atinse', exact: true }).getAttribute('aria-pressed'), 'true'); checks++;
   await panel.getByRole('button', { name: 'Arhivează ' + name, exact: true }).click();
   await panel.getByRole('button', { name: 'Reactivează ' + name, exact: true }).waitFor();
   assert.equal(await panel.getByRole('button', { name: 'Contribuie la ' + name, exact: true }).count(), 0); checks++;
   await panel.getByRole('button', { name: 'Reactivează ' + name, exact: true }).click();
   await panel.getByRole('button', { name: 'Editează obiectiv ' + name, exact: true }).click();
   await panel.getByRole('button', { name: 'Șterge obiectivul', exact: true }).click();
   await page.getByRole('button', { name: 'Anulează', exact: true }).click();
   assert.ok(goal); checks++;
   await panel.getByRole('button', { name: 'Șterge obiectivul', exact: true }).click();
   await page.getByRole('dialog').getByRole('button', { name: 'Șterge obiectivul', exact: true }).click();
   await panel.getByText(/Primul pas: alege o țintă/).waitFor();
   assert.equal(goal, null); assert.equal(entries.length, 0); assert.deepEqual(errors, []); checks += 3;
   await context.close();
  }
  console.log('PASS: ' + checks + ' savings-goal assertions: simulator, persistence, idempotent retries, history, conflicts, completion, archive, mobile and WCAG');
 } finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
