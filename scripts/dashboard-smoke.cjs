'use strict';
// Browser behavior uses a synthetic API; backend/ownership is verified separately.
const http = require('node:http'), fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const deps = path.resolve(__dirname, '../expense-tracker-ui');
const { chromium } = require(require.resolve('playwright', { paths: [deps] }));
const AxeBuilder = require(require.resolve('@axe-core/playwright', { paths: [deps] })).default;
const root = path.join(deps, 'dist');
const screenshotRoot = path.resolve(__dirname, '../.artifacts/screenshots');
fs.mkdirSync(screenshotRoot, { recursive: true });
function date(offset = 0) { const value = new Date(); value.setUTCDate(value.getUTCDate() + offset); return value.toISOString().slice(0, 10); }
function item(id, name, extra = {}) { return { id, name, cost: 10, currency: 'EUR', billingPeriod: 'Monthly', nextBillingDate: date(2), category: 'SaaS', isActive: true, ...extra }; }
function month(offset) { const value = new Date(); value.setUTCDate(1); value.setUTCMonth(value.getUTCMonth() + offset); return value.toISOString().slice(0, 7); }
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
  for (const width of [1440, 390, 320]) for (const theme of ['light', 'dark']) {
   const context = await browser.newContext({ viewport: { width, height: 950 }, serviceWorkers: 'block', reducedMotion: 'reduce' });
   await context.addInitScript(value => localStorage.setItem('theme', value), theme);
   const page = await context.newPage(), errors = []; page.on('pageerror', error => errors.push(error.message));
   await page.route('https://analytics.micutu.com/**', route => route.abort());
   let items = Array.from({ length: 13 }, (_, i) => item(`00000000-0000-0000-0000-${String(i + 1).padStart(12, '0')}`, i === 1 ? 'Alpha service' : i === 0 ? 'Long' + 'x'.repeat(96) : `Service ${i + 1}`, i === 12 ? { currency: 'XTS' } : {}));
   let account = { username: 'dashboard-fixture', email: 'fixture@example.invalid', emailVerified: true, createdAt: new Date().toISOString(), pendingEmail: null, pendingEmailExpiresAt: null, emailRetryAfterSeconds: 0 };
   let posts = 0, slowDone; const slowFinished = new Promise(resolve => { slowDone = resolve; });
   let sessions = [{ id: 'current', isCurrent: true, createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() }, { id: 'other', isCurrent: false, createdAt: new Date(Date.now() - 86400000).toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() }];
   await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()); let data = {}, status = 200;
    const active = items.filter(s => s.isActive);
    const byCurrency = [...new Set(active.map(s => s.currency))].map(currency => ({ currency, activeCount: active.filter(s => s.currency === currency).length, monthlyTotal: active.filter(s => s.currency === currency).reduce((sum, s) => sum + s.cost, 0), yearlyTotal: active.filter(s => s.currency === currency).reduce((sum, s) => sum + s.cost * 12, 0) }));
    if (url.pathname === '/api/auth/me') data = { username: 'dashboard-fixture' };
    else if (url.pathname === '/api/auth/logout') { status = 204; data = null; }
    else if (url.pathname === '/api/account') data = account;
    else if (url.pathname === '/api/account/change-email') {
     account.pendingEmail = request.postDataJSON().email;
     account.pendingEmailExpiresAt = new Date(Date.now() + 900000).toISOString(); account.emailRetryAfterSeconds = 2;
     data = { pendingEmail: account.pendingEmail, expiresAt: account.pendingEmailExpiresAt, retryAfterSeconds: 2 };
    } else if (url.pathname === '/api/account/pending-email') { account.pendingEmail = null; account.pendingEmailExpiresAt = null; status = 204; }
    else if (url.pathname === '/api/account/confirm-email-change') {
     if (request.postDataJSON().code !== '123456') { status = 400; data = { title: 'Cod sintetic greșit.' }; }
     else { account.email = account.pendingEmail; account.pendingEmail = null; account.pendingEmailExpiresAt = null; data = { message: 'Email confirmat.' }; }
    }
    else if (url.pathname === '/api/account/sessions') data = sessions;
    else if (url.pathname === '/api/account/sessions/other') { sessions = sessions.filter(s => s.id !== 'other'); status = 204; }
    else if (url.pathname === '/api/account/finance-tokens') data = { available: true, tokens: [] };
    else if (url.pathname === '/api/subscriptions/rates/status') data = { rates: { RON: 1, EUR: .2, USD: .22 }, source: 'live', fetchedAt: new Date().toISOString() };
    else if (url.pathname === '/api/subscriptions/summary') data = { byCurrency, activeSubscriptions: active.length, totalSubscriptions: items.length, dueThisWeek: active.length };
    else if (url.pathname === '/api/subscriptions/insights') data = {
     asOfDate: date(), activeCount: active.length, inactiveCount: items.length - active.length, overdueCount: 1,
     overdue: [{ ...items[0], date: date(-2) }], upcoming: active.slice(0, 12).map(s => ({ ...s, date: date(2) })),
     byCategory: byCurrency.map(c => ({ category: 'SaaS', currency: c.currency, monthlyTotal: c.monthlyTotal })),
     forecast: Array.from({ length: 6 }, (_, i) => ({ month: month(i), paymentCount: active.length, byCurrency: byCurrency.map(c => ({ currency: c.currency, amount: c.monthlyTotal })) })),
     pausedMonthly: [], savingsOpportunities: active.slice(1, 4).map(s => ({ id: s.id, name: s.name, currency: s.currency, monthlyTotal: s.cost }))
    };
    else if (url.pathname === '/api/subscriptions/calendar') { await route.fulfill({ status: 200, contentType: 'text/calendar', headers: { 'Content-Disposition': 'attachment; filename="abonamente.ics"' }, body: 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n' }); return; }
    else if (/\/api\/subscriptions\/[^/]+\/status$/.test(url.pathname)) { const id = url.pathname.split('/').at(-2); const sub = items.find(s => s.id === id); sub.isActive = request.postDataJSON().isActive; data = sub; }
    else if (url.pathname === '/api/subscriptions' && request.method() === 'POST') {
     posts++; await new Promise(resolve => setTimeout(resolve, 150));
     if (posts === 1) { status = 400; data = { detail: 'Respins pentru verificarea păstrării formularului.' }; }
     else { data = item('new-item', request.postDataJSON().name, request.postDataJSON()); items.push(data); status = 201; }
    } else if (url.pathname === '/api/subscriptions') {
     const search = url.searchParams.get('search') ?? '';
     if (search === 'slow' || search === 'fast') {
      data = { items: [item(search, search === 'slow' ? 'Slow result' : 'Fast result')], total: 1 };
      if (search === 'slow') { await new Promise(resolve => setTimeout(resolve, 1100)); await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) }); slowDone(); return; }
     } else {
      let result = items.filter(s => s.name.toLowerCase().includes(search.toLowerCase()));
      const state = url.searchParams.get('status'); if (state === 'active') result = result.filter(s => s.isActive); if (state === 'inactive') result = result.filter(s => !s.isActive);
      const skip = Number(url.searchParams.get('skip') ?? 0), take = Number(url.searchParams.get('take') ?? 10); data = { items: result.slice(skip, skip + take), total: result.length };
     }
    }
    await route.fulfill({ status, contentType: 'application/json', body: status === 204 ? '' : JSON.stringify(data) });
   });
   await page.goto(`http://127.0.0.1:${server.address().port}`);
   await page.getByRole('heading', { name: 'Planifică din timp' }).waitFor();
   assert.equal(await page.locator('link[rel=canonical]').getAttribute('href'), 'https://expenses.micutu.com/'); checks++;
   assert.equal(await page.locator('meta[name=robots]').getAttribute('content'), 'noindex,nofollow'); checks++;
   await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).waitFor();
   const noOverflow = async () => assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Overflow at ${width}/${theme}`);
   await noOverflow(); checks++;
   assert.ok(await page.getByText('Fără curs disponibil:', { exact: false }).isVisible()); checks++;
   await page.screenshot({ path: path.join(screenshotRoot, `dashboard-${width}-${theme}.png`), fullPage: true });
   const forecastButtons = page.getByRole('group', { name: 'Alege luna prognozei' }).getByRole('button');
   assert.equal(await forecastButtons.count(), 6); checks++;
   await forecastButtons.nth(1).click(); assert.equal(await forecastButtons.nth(1).getAttribute('aria-pressed'), 'true'); checks++;
   await page.getByRole('checkbox', { name: /Alpha service/ }).check();
   assert.ok(await page.getByText('10,00 EUR / lună · aproximativ 120,00 EUR / an', { exact: true }).isVisible()); checks++;
   const downloadEvent = page.waitForEvent('download'); await page.getByRole('button', { name: 'Exportă calendar .ics', exact: true }).click();
   assert.equal((await downloadEvent).suggestedFilename(), 'abonamente.ics'); checks++;
   await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).click(); await page.getByRole('dialog').waitFor();
   await page.keyboard.press('Escape'); await page.getByRole('dialog').waitFor({ state: 'detached' });
   assert.equal(items[1].isActive, true); checks++;
   await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).click();
   await page.getByRole('button', { name: 'Confirmă', exact: true }).click();
   await page.getByRole('button', { name: 'Reactivează Alpha service', exact: true }).waitFor(); assert.equal(items[1].isActive, false); checks++;
   await page.getByRole('button', { name: 'Reactivează Alpha service', exact: true }).click(); await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).waitFor(); checks++;
   await page.getByRole('button', { name: 'Duplică Alpha service', exact: true }).click();
   assert.equal(await page.getByLabel('Nume', { exact: true }).inputValue(), 'Alpha service (copie)'); assert.equal(posts, 0); checks++;
   await page.getByRole('button', { name: 'Adaugă abonament', exact: true }).evaluate(button => { button.click(); button.click(); });
   await page.getByText('Respins pentru verificarea păstrării formularului.', { exact: true }).waitFor();
   assert.equal(posts, 1); assert.equal(await page.getByLabel('Nume', { exact: true }).inputValue(), 'Alpha service (copie)'); checks++;
   await page.getByRole('button', { name: 'Adaugă abonament', exact: true }).click(); await page.locator('#subscription-form').waitFor({ state: 'detached' }); assert.equal(posts, 2); checks++;
   const slowRequest = page.waitForRequest(request => new URL(request.url()).searchParams.get('search') === 'slow');
   await page.getByLabel('Caută abonamente sau categorii', { exact: true }).fill('slow'); await slowRequest;
   await page.getByLabel('Caută abonamente sau categorii', { exact: true }).fill('fast'); await page.getByRole('heading', { name: 'Fast result', exact: true }).waitFor();
   await slowFinished; assert.equal(await page.getByRole('heading', { name: 'Slow result', exact: true }).count(), 0); checks++;
   await page.getByLabel('Caută abonamente sau categorii', { exact: true }).fill(''); await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).waitFor();
   const audit = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), [], `Axe at ${width}/${theme}`); checks++;
   await page.getByRole('button', { name: 'Setări cont', exact: true }).click(); await page.getByRole('heading', { name: 'Sesiuni active', exact: true }).waitFor();
   const dialog = page.getByRole('dialog'); await noOverflow(); checks++;
   await dialog.getByRole('button').last().focus(); await page.keyboard.press('Tab');
   assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), 'Închide'); checks++;
   await page.getByRole('button', { name: /^Revocă sesiunea din/ }).click();
   await page.getByRole('button', { name: /^Revocă sesiunea din/ }).waitFor({ state: 'detached' }); assert.equal(sessions.length, 1); checks++;
   await page.getByLabel('Noua adresă de email', { exact: true }).fill('new-inbox@example.invalid');
   await page.getByLabel('Parola pentru schimbarea emailului', { exact: true }).fill('synthetic-current-password');
   await page.getByRole('button', { name: 'Trimite codul', exact: true }).click();
   await page.getByRole('heading', { name: 'Confirmă noua adresă', exact: true }).waitFor();
   assert.equal(account.email, 'fixture@example.invalid'); checks++;
   assert.ok(await dialog.getByText('fixture@example.invalid', { exact: true }).isVisible()); checks++;
   assert.ok(await page.getByRole('button', { name: /Cod nou disponibil în/ }).isDisabled()); checks++;
   await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'detached' });
   await page.getByRole('button', { name: 'Setări cont', exact: true }).click();
   await page.getByRole('heading', { name: 'Confirmă noua adresă', exact: true }).waitFor();
   assert.equal(await page.getByLabel('Noua adresă de email', { exact: true }).inputValue(), 'new-inbox@example.invalid'); checks++;
   const pendingAudit = await new AxeBuilder({ page }).include('[role="dialog"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(pendingAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), []); checks++;
   await noOverflow(); checks++;
   await page.getByRole('button', { name: 'Anulează schimbarea emailului', exact: true }).click();
   await page.getByRole('heading', { name: 'Confirmă noua adresă', exact: true }).waitFor({ state: 'detached' });
   assert.equal(account.email, 'fixture@example.invalid'); checks++;
   await page.getByLabel('Noua adresă de email', { exact: true }).fill('confirmed-inbox@example.invalid');
   await page.getByLabel('Parola pentru schimbarea emailului', { exact: true }).fill('synthetic-current-password');
   await page.getByRole('button', { name: 'Trimite codul', exact: true }).click();
   await page.getByLabel('Codul pentru noua adresă de email', { exact: true }).fill('000000');
   await page.getByRole('button', { name: 'Confirmă noul email', exact: true }).click();
   await page.getByText('Cod sintetic greșit.', { exact: true }).waitFor();
   assert.equal(await page.getByLabel('Codul pentru noua adresă de email', { exact: true }).inputValue(), '000000'); checks++;
   await page.getByLabel('Codul pentru noua adresă de email', { exact: true }).fill('123456');
   await page.getByRole('button', { name: 'Confirmă noul email', exact: true }).click();
   await page.getByRole('heading', { name: 'Confirmă noua adresă', exact: true }).waitFor({ state: 'detached' });
   assert.ok(await dialog.getByText('confirmed-inbox@example.invalid', { exact: true }).isVisible()); checks++;
   const settingsAudit = await new AxeBuilder({ page }).include('[role="dialog"]').withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
   assert.deepEqual(settingsAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), [], `Settings axe at ${width}/${theme}`); checks++;
   await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'detached' });
   assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), 'Setări cont'); checks++;
   await page.getByRole('button', { name: 'Ieșire', exact: true }).click();
   await page.getByRole('heading', { name: 'Expense Tracker', exact: true }).waitFor();
   assert.equal(await page.getByRole('heading', { name: 'Alpha service', exact: true }).count(), 0); checks++;
   assert.equal(await page.locator('meta[name=robots]').getAttribute('content'), 'index,follow'); checks++;
   const loginAudit = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
   assert.deepEqual(loginAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), [], `Login axe at ${width}/${theme}`); checks++;
   await page.getByRole('button', { name: 'Prima dată? Creează un cont', exact: true }).click();
   await page.getByLabel('Email', { exact: true }).waitFor();
   const registerAudit = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
   assert.deepEqual(registerAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), [], `Register axe at ${width}/${theme}`); checks++;

   assert.deepEqual(errors, []); checks++;
   await context.close();
  }
  // Two tabs see the same expired session. The origin-wide Web Lock must
  // rotate the shared refresh cookie once, then let both tabs resume.
  const shared = await browser.newContext({ serviceWorkers: 'block' });
  let rotations = 0, refreshed = false, initial = 0, releaseInitial;
  const initialBarrier = new Promise(resolve => { releaseInitial = resolve; });
  await shared.route('https://analytics.micutu.com/**', route => route.abort());
  await shared.route('**/api/**', async route => {
   const pathname = new URL(route.request().url()).pathname;
   let data = {}, status = 200;
   if (pathname === '/api/auth/me') {
    if (!refreshed) { initial++; if (initial <= 2) { if (initial === 2) releaseInitial(); await initialBarrier; } status = 401; }
    else data = { username: 'shared-tabs' };
   } else if (pathname === '/api/auth/refresh') { rotations++; await new Promise(resolve => setTimeout(resolve, 100)); refreshed = true; data = { username: 'shared-tabs' }; }
   else if (pathname === '/api/subscriptions') data = { items: [], total: 0 };
   else if (pathname === '/api/subscriptions/summary') data = { byCurrency: [], totalSubscriptions: 0, activeSubscriptions: 0, dueThisWeek: 0 };
   else if (pathname === '/api/subscriptions/insights') data = { forecast: [], byCategory: [], upcoming: [], overdue: [], savingsOpportunities: [] };
   await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(data) });
  });
  const tabOne = await shared.newPage(), tabTwo = await shared.newPage();
  await Promise.all([tabOne.goto(`http://127.0.0.1:${server.address().port}`), tabTwo.goto(`http://127.0.0.1:${server.address().port}`)]);
  await Promise.all([tabOne.getByRole('heading', { name: 'Bună, shared-tabs.' }).waitFor(), tabTwo.getByRole('heading', { name: 'Bună, shared-tabs.' }).waitFor()]);
  assert.equal(rotations, 1); checks++;
  await shared.close();
  console.log(`PASS: ${checks} dashboard assertions at 1440/390/320 px in light/dark, including planning, savings, pause, duplicate, form preservation, stale-response protection, calendar, sessions, keyboard and WCAG`);
 } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
