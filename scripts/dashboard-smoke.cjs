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
function item(id, name, extra = {}) { return { id, name, cost: 10, currency: 'EUR', billingPeriod: 'Monthly', nextBillingDate: date(2), category: 'SaaS', isActive: true, updatedAt: '2026-11-01T10:00:00Z', ...extra }; }
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
  const widths = (process.env.EXPENSE_UI_WIDTHS ?? '1440,390,320').split(',').map(Number);
  const themes = (process.env.EXPENSE_UI_THEMES ?? 'light,dark').split(',');
  for (const width of widths) for (const theme of themes) {
   const context = await browser.newContext({ viewport: { width, height: 950 }, serviceWorkers: 'block', reducedMotion: 'reduce' });
   await context.addInitScript(value => localStorage.setItem('theme', value), theme);
   const page = await context.newPage(), errors = []; page.on('pageerror', error => errors.push(error.message));
   await page.route('https://analytics.micutu.com/**', route => route.abort());
   let items = Array.from({ length: 13 }, (_, i) => item(`00000000-0000-0000-0000-${String(i + 1).padStart(12, '0')}`, i === 1 ? 'Alpha service' : i === 0 ? 'Long' + 'x'.repeat(96) : `Service ${i + 1}`, i === 12 ? { currency: 'XTS' } : {}));
   const noteLiteral = '<img src=x onerror="window.__noteExecuted=true">';
   items[2].details = { notes: noteLiteral, providerUrl: 'https://supplier.example.invalid/account', trialEndsOn: date(2) };
   items[3].details = { notes: '', providerUrl: 'javascript:window.__unsafeLink=true', trialEndsOn: null };
   let account = { username: 'dashboard-fixture', email: 'fixture@example.invalid', emailVerified: true, createdAt: new Date().toISOString(), pendingEmail: null, pendingEmailExpiresAt: null, emailRetryAfterSeconds: 0 };
   let budgets = [], budgetRevision = 1;
   let historyFailures = 1, staleUpdates = 0;
   const priceChanges = Array.from({ length: 25 }, (_, i) => ({ id: `change-${i}`, changedAt: '2026-11-14T12:00:00Z',
    previous: { cost: 10, currency: 'EUR', billingPeriod: 'Monthly' }, current: { cost: 12, currency: i === 1 ? 'USD' : 'EUR', billingPeriod: 'Monthly' },
    monthlyDelta: i === 1 ? null : 2, changePercent: i === 1 ? null : 20 }));
   const budgetVersion = () => `11111111-1111-1111-1111-${String(budgetRevision).padStart(12, "0")}`;
   let posts = 0, slowDone; const slowFinished = new Promise(resolve => { slowDone = resolve; });
   let sessions = [{ id: 'current', isCurrent: true, createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() }, { id: 'other', isCurrent: false, createdAt: new Date(Date.now() - 86400000).toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() }];
   await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()); let data = {}, status = 200;
    const active = items.filter(s => s.isActive);
    const byCurrency = [...new Set(active.map(s => s.currency))].map(currency => ({ currency, activeCount: active.filter(s => s.currency === currency).length, monthlyTotal: active.filter(s => s.currency === currency).reduce((sum, s) => sum + s.cost, 0), yearlyTotal: active.filter(s => s.currency === currency).reduce((sum, s) => sum + s.cost * 12, 0) }));
    if (url.pathname === '/api/goals') data = [];
    else if (url.pathname === '/api/auth/me') data = { username: 'dashboard-fixture', accountContext: 'A'.repeat(64) };
    else if (url.pathname === '/api/auth/logout') { status = 204; data = null; }
    else if (url.pathname === '/api/budgets') {
     if (request.method() === 'POST') { budgets = [{ id: 'budget-fixture', ...request.postDataJSON(), version: budgetVersion(), updatedAt: new Date().toISOString() }]; data = { id: budgets[0].id, version: budgets[0].version }; status = 201; }
     else data = budgets.map(b => { const monthlyPlanned = active.filter(s => s.currency === b.currency && (!b.category || s.category === b.category)).reduce((sum, s) => sum + s.cost, 0); return { ...b, monthlyPlanned, remaining: b.monthlyLimit - monthlyPlanned, usagePercent: monthlyPlanned / b.monthlyLimit * 100, isOverBudget: monthlyPlanned > b.monthlyLimit }; });
    } else if (url.pathname === '/api/budgets/budget-fixture') {
     const version = request.method() === 'DELETE' ? url.searchParams.get('version') : request.postDataJSON().version;
     if (version !== budgets[0]?.version) { status = 409; data = { title: 'Bugetul s-a schimbat într-un alt tab.' }; }
     else if (request.method() === 'DELETE') { budgets = []; status = 204; }
     else { budgetRevision++; budgets[0] = { ...budgets[0], ...request.postDataJSON(), version: budgetVersion() }; data = { id: budgets[0].id, version: budgets[0].version }; }
    }
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
    else if (url.pathname === '/api/subscriptions/export') {
     if (url.searchParams.get('pageOnly') !== 'true') { status = 413; data = { title: 'Synthetic complete export limit' }; }
     else { await route.fulfill({ status: 200, contentType: 'text/csv', body: 'Id,Name,Cost,NextBillingDate,FormatVersion\n11111111-1111-1111-1111-111111111111,Portable,10,2027-01-01,2\n' }); return; }
    }
    else if (url.pathname === '/api/subscriptions/duplicates') data = { checkedCount: items.length, includeInactive: url.searchParams.get('includeInactive') === 'true', groups: [{ name: 'Alpha service', currency: 'EUR', activeMonthlyTotal: 20, items: [items[1], { ...items[1], id: 'duplicate-alpha', cost: 120, billingPeriod: 'Yearly' }] }] };
    else if (/^\/api\/subscriptions\/[0-9a-f-]{36}$/.test(url.pathname) && request.method() === 'GET') data = items.find(s => s.id === url.pathname.split('/').at(-1));
    else if (/^\/api\/subscriptions\/[^/]+\/price-history$/.test(url.pathname)) {
     const id = url.pathname.split('/').at(-2), sub = items.find(s => s.id === id);
     const skip = Number(url.searchParams.get('skip') ?? 0), take = Number(url.searchParams.get('take') ?? 20);
     if (historyFailures-- > 0) { status = 503; data = { title: 'Temporary history failure' }; }
     else { const all = sub.name === 'Alpha service' ? priceChanges : []; data = { subscriptionId: id, current: { cost: sub.cost, currency: sub.currency, billingPeriod: sub.billingPeriod }, total: all.length, skip, take, entries: all.slice(skip, skip + take) }; }
    }
    else if (/^\/api\/subscriptions\/[^/]+$/.test(url.pathname) && request.method() === 'PUT') {
     const sub = items.find(s => s.id === url.pathname.split('/').at(-1));
     assert.equal(request.postDataJSON().expectedUpdatedAt, sub.updatedAt);
     if (sub.id === items[2].id) { Object.assign(sub, request.postDataJSON(), { updatedAt: new Date().toISOString() }); data = sub; }
     else { staleUpdates++; status = 409; data = { detail: 'Abonamentul a fost modificat între timp. Redeschide versiunea actuală.' }; }
    }
    else if (url.pathname === '/api/subscriptions/insights') data = {
     asOfDate: date(), activeCount: active.length, inactiveCount: items.length - active.length, overdueCount: 1,
     overdue: [{ ...items[0], date: date(-2) }], upcoming: active.slice(0, 12).map(s => ({ ...s, date: date(2) })),
     byCategory: byCurrency.map(c => ({ category: 'SaaS', currency: c.currency, monthlyTotal: c.monthlyTotal })),
     forecast: Array.from({ length: 6 }, (_, i) => ({ month: month(i), paymentCount: active.length, byCurrency: byCurrency.map(c => ({ currency: c.currency, amount: c.monthlyTotal })) })),
     pausedMonthly: [], savingsOpportunities: active.slice(1, 4).map(s => ({ id: s.id, name: s.name, currency: s.currency, monthlyTotal: s.cost })),
     trialCount: 1, trials: [{ id: items[2].id, name: items[2].name, endsOn: items[2].details.trialEndsOn, daysRemaining: Math.round((Date.parse(items[2].details.trialEndsOn) - Date.parse(date())) / 86400000), cost: items[2].cost, currency: items[2].currency, billingPeriod: items[2].billingPeriod }]
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
   const provider = page.getByRole('link', { name: 'Pagina furnizorului Service 3: supplier.example.invalid', exact: true });
   assert.equal(await provider.getAttribute('href'), 'https://supplier.example.invalid/account'); checks++;
   assert.equal(await provider.getAttribute('rel'), 'noopener noreferrer'); assert.equal(await provider.getAttribute('referrerpolicy'), 'no-referrer'); checks++;
   assert.equal(await page.locator('a[href^="javascript:"]').count(), 0); checks++;
   await page.getByText('Note despre Service 3', { exact: true }).click();
   assert.ok(await page.getByText(noteLiteral, { exact: true }).isVisible());
   assert.equal(await page.evaluate(() => window.__noteExecuted), undefined); checks++;
   await page.getByRole('button', { name: 'Revizuiește proba Service 3', exact: true }).click();
   assert.equal(await page.getByLabel('Note personale (opțional)', { exact: true }).inputValue(), noteLiteral); checks++;
   assert.equal(await page.getByLabel('Pagina furnizorului (opțional)', { exact: true }).inputValue(), 'https://supplier.example.invalid/account'); checks++;
   await page.getByLabel('Final perioadă de probă (opțional)', { exact: true }).fill(date(3));
   assert.equal(await page.getByLabel('Următoarea plată', { exact: true }).evaluate(input => input.checkValidity()), false); checks++;
   await page.getByLabel('Următoarea plată', { exact: true }).fill(date(3));
   await page.getByLabel('Note personale (opțional)', { exact: true }).fill('Plan probă\nCondiții verificate.');
   const detailsAudit = await new AxeBuilder({ page }).include('#subscription-form').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(detailsAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), []); checks++;
   await page.getByRole('button', { name: 'Salvează modificările', exact: true }).click();
   await page.locator('#subscription-form').waitFor({ state: 'detached' });
   assert.equal(items[2].details.trialEndsOn, date(3)); assert.equal(items[2].details.notes, 'Plan probă\nCondiții verificate.'); checks++;
   await page.getByText('Se încheie în 3 zile', { exact: false }).waitFor(); checks++;
   await page.getByRole('button', { name: 'Export CSV', exact: true }).click();
   await page.getByText('Exportul complet depășește 1 MB. Alege CSV: pagina curentă și exportă pe rând paginile.', { exact: true }).waitFor(); checks++;
   await page.getByLabel('Conținut export CSV', { exact: true }).selectOption('page');
   const csvDownload = page.waitForEvent('download');
   const csvRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/api/subscriptions/export' && new URL(r.url()).searchParams.get('pageOnly') === 'true');
   await page.getByRole('button', { name: 'Export CSV', exact: true }).click();
   const exportQuery = new URL((await csvRequest).url()).searchParams;
   assert.equal(exportQuery.get('skip'), '0'); assert.equal(exportQuery.get('take'), '10'); checks++;
   assert.match((await csvDownload).suggestedFilename(), /^abonamente_pagina_.*\.csv$/); checks++;
   await page.getByRole('button', { name: 'Arată 1 grup posibil duplicat', exact: true }).click();
   await page.getByRole('button', { name: 'Revizuiește Alpha service 120,00 EUR', exact: true }).waitFor(); checks++;
   const includePausedRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/api/subscriptions/duplicates' && new URL(r.url()).searchParams.get('includeInactive') === 'true');
   await page.getByLabel('Include și abonamentele suspendate', { exact: true }).check(); await includePausedRequest; checks++;
   await page.getByRole('button', { name: 'Revizuiește Alpha service 10,00 EUR', exact: true }).click();
   assert.equal(await page.getByLabel('Nume', { exact: true }).inputValue(), 'Alpha service'); checks++;
   await page.getByRole('button', { name: 'Anulează', exact: true }).click();
   await page.getByRole('button', { name: 'Ascunde 1 grup posibil duplicat', exact: true }).click();
   const historyButton = page.getByRole('button', { name: 'Istoric preț Alpha service', exact: true });
   await historyButton.click();
   const priceDialog = page.getByRole('dialog', { name: 'Istoric preț', exact: true });
   await priceDialog.getByRole('alert').waitFor(); checks++;
   await priceDialog.getByRole('button', { name: 'Reîncarcă istoricul', exact: true }).click();
   await priceDialog.getByText('25 modificări înregistrate', { exact: true }).waitFor();
   assert.equal(await priceDialog.getByRole('listitem').count(), 20); checks++;
   assert.ok(await priceDialog.getByText('+2,00 EUR / lună echivalent · +20,00%', { exact: true }).first().isVisible()); checks++;
   assert.ok(await priceDialog.getByText('Valută schimbată; variația nu se compară automat.', { exact: true }).isVisible()); checks++;
   await priceDialog.getByRole('button', { name: 'Mai vechi', exact: true }).click();
   await priceDialog.getByText('21–25 din 25', { exact: true }).waitFor();
   assert.equal(await priceDialog.getByRole('listitem').count(), 5); checks++;
   assert.ok(await priceDialog.getByRole('button', { name: 'Mai vechi', exact: true }).isDisabled()); checks++;
   await priceDialog.getByRole('button', { name: 'Mai noi', exact: true }).click();
   await priceDialog.getByText('1–20 din 25', { exact: true }).waitFor();
   assert.ok(await priceDialog.getByRole('button', { name: 'Mai noi', exact: true }).isDisabled()); checks++;
   await noOverflow(); checks++;
   const historyAudit = await new AxeBuilder({ page }).include('[role="dialog"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(historyAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), [], `History axe at ${width}/${theme}`); checks++;
   await page.screenshot({ path: path.join(screenshotRoot, `price-history-${width}-${theme}.png`) });
   await priceDialog.getByRole('button').last().focus(); await page.keyboard.press('Tab');
   assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), 'Închide istoricul'); checks++;
   await page.keyboard.press('Escape'); await priceDialog.waitFor({ state: 'detached' });
   assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), 'Istoric preț Alpha service'); checks++;
   await page.getByRole('button', { name: `Istoric preț ${items[0].name}`, exact: true }).click();
   await priceDialog.getByText('Nu există încă modificări de preț înregistrate.', { exact: true }).waitFor(); checks++;
   await page.getByRole('button', { name: 'Închide istoricul', exact: true }).click();
   await page.getByRole('button', { name: 'Editează Alpha service', exact: true }).click();
   await page.getByLabel('Cost', { exact: true }).fill('77');
   await page.getByRole('button', { name: 'Salvează modificările', exact: true }).click();
   await page.getByText('Abonamentul a fost modificat între timp. Redeschide versiunea actuală.', { exact: true }).waitFor();
   assert.equal(staleUpdates, 1); assert.equal(await page.getByLabel('Cost', { exact: true }).inputValue(), '77'); checks++;
   await page.getByRole('button', { name: 'Anulează', exact: true }).click();
   assert.ok(await page.getByText('Fără curs disponibil:', { exact: false }).isVisible()); checks++;
   await page.screenshot({ path: path.join(screenshotRoot, `dashboard-${width}-${theme}.png`), fullPage: true });
   const forecastButtons = page.getByRole('group', { name: 'Alege luna prognozei' }).getByRole('button');
   assert.equal(await forecastButtons.count(), 6); checks++;
   await forecastButtons.nth(1).click(); assert.equal(await forecastButtons.nth(1).getAttribute('aria-pressed'), 'true'); checks++;
   await page.getByRole('checkbox', { name: /Alpha service/ }).check();
   assert.ok(await page.getByText('10,00 EUR / lună · aproximativ 120,00 EUR / an', { exact: true }).isVisible()); checks++;
   const downloadEvent = page.waitForEvent('download'); await page.getByRole('button', { name: 'Exportă calendar .ics', exact: true }).click();
   assert.equal((await downloadEvent).suggestedFilename(), 'abonamente.ics'); checks++;
   await page.getByLabel('Valuta bugetului', { exact: true }).selectOption('EUR');
   await page.getByLabel('Limita lunară a bugetului', { exact: true }).fill('100');
   await page.getByRole('button', { name: 'Creează buget', exact: true }).click();
   const budgetCard = page.getByRole('article', { name: 'Buget EUR general', exact: true });
   await budgetCard.getByText('Depășit cu 20,00 EUR', { exact: true }).waitFor(); checks++;
   assert.equal(await budgetCard.getByRole('progressbar').getAttribute('aria-valuenow'), '100'); checks++;
   await page.reload(); await budgetCard.getByText('Depășit cu 20,00 EUR', { exact: true }).waitFor(); checks++;
   const budgetAudit = await new AxeBuilder({ page }).include('section[aria-labelledby="budgets-title"]').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
   assert.deepEqual(budgetAudit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), []); checks++;
   budgetRevision++; budgets[0].version = budgetVersion(); budgets[0].monthlyLimit = 120;
   await page.getByRole('button', { name: 'Editează buget EUR general', exact: true }).click();
   await page.getByLabel('Limita lunară a bugetului', { exact: true }).fill('150');
   await page.getByRole('button', { name: 'Salvează bugetul', exact: true }).click();
   await page.getByText('Bugetul s-a schimbat într-un alt tab.', { exact: true }).waitFor();
   assert.equal(await page.getByLabel('Limita lunară a bugetului', { exact: true }).inputValue(), '150'); assert.equal(budgets[0].monthlyLimit, 120); checks++;
   await page.getByRole('button', { name: 'Reîncarcă bugetele', exact: true }).click();
   await budgetCard.getByText('Disponibil în buget: 0,00 EUR', { exact: true }).waitFor();
   await page.getByRole('button', { name: 'Editează buget EUR general', exact: true }).click();
   await page.getByLabel('Limita lunară a bugetului', { exact: true }).fill('200');
   await page.getByRole('button', { name: 'Salvează bugetul', exact: true }).click();
   await budgetCard.getByText('Disponibil în buget: 80,00 EUR', { exact: true }).waitFor(); checks++;
   await noOverflow(); checks++;
   await page.getByRole('button', { name: 'Șterge buget EUR general', exact: true }).click();
   await page.getByRole('button', { name: 'Șterge bugetul', exact: true }).click();
   await budgetCard.waitFor({ state: 'detached' }); assert.equal(items.length, 13); checks++;
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
   await page.getByLabel('Caută abonamente, categorii sau note', { exact: true }).fill('slow'); await slowRequest;
   await page.getByLabel('Caută abonamente, categorii sau note', { exact: true }).fill('fast'); await page.getByRole('heading', { name: 'Fast result', exact: true }).waitFor();
   await slowFinished; assert.equal(await page.getByRole('heading', { name: 'Slow result', exact: true }).count(), 0); checks++;
   await page.getByLabel('Caută abonamente, categorii sau note', { exact: true }).fill(''); await page.getByRole('button', { name: 'Suspendă Alpha service', exact: true }).waitFor();
   await page.waitForFunction(() => document.querySelector('section[aria-labelledby="budgets-title"] button')?.disabled === false);
   const audit = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
   assert.deepEqual(audit.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => ({ target: n.target, html: n.html, failure: n.failureSummary })) })), [], `Axe at ${width}/${theme}`); checks++;
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
    else data = { username: 'shared-tabs', accountContext: 'A'.repeat(64) };
   } else if (pathname === '/api/auth/refresh') { rotations++; await new Promise(resolve => setTimeout(resolve, 100)); refreshed = true; data = { username: 'shared-tabs', accountContext: 'A'.repeat(64) }; }
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
  // A response authorized before logout must not download private data into
  // the browser after the UI has switched away from that account.
  for (const resource of ['csv', 'calendar']) {
   const raceContext = await browser.newContext({ serviceWorkers: 'block' });
   const racePage = await raceContext.newPage(), downloads = [];
   racePage.on('download', event => downloads.push(event));
   let releaseResponse, markRequested;
   const gate = new Promise(resolve => { releaseResponse = resolve; });
   const requested = new Promise(resolve => { markRequested = resolve; });
   const exportPath = resource === 'csv' ? '/api/subscriptions/export' : '/api/subscriptions/calendar';
   await racePage.route('https://analytics.micutu.com/**', route => route.abort());
   await racePage.route('**/api/**', async route => {
    const pathname = new URL(route.request().url()).pathname; let data = {}, status = 200;
    if (pathname === exportPath) {
     markRequested(); await gate;
     await route.fulfill({ status: 200, contentType: resource === 'csv' ? 'text/csv' : 'text/calendar', body: 'PRIVATE_SYNTHETIC_EXPORT' }); return;
    }
    if (pathname === '/api/auth/me') data = { username: 'export-race', accountContext: 'A'.repeat(64) };
    else if (pathname === '/api/auth/logout') status = 204;
    else if (pathname === '/api/account') data = { username: 'export-race', email: 'synthetic@example.invalid', emailVerified: true };
    else if (pathname === '/api/subscriptions') data = { items: [], total: 0 };
    else if (pathname === '/api/subscriptions/summary') data = { byCurrency: [], totalSubscriptions: 0, activeSubscriptions: 0, dueThisWeek: 0 };
    else if (pathname === '/api/subscriptions/insights') data = { forecast: [], byCategory: [], upcoming: [], overdue: [], savingsOpportunities: [] };
    else if (pathname === '/api/budgets') data = [];
    else if (pathname === '/api/subscriptions/duplicates') data = { checkedCount: 0, groups: [] };
    await route.fulfill({ status, contentType: 'application/json', body: status === 204 ? '' : JSON.stringify(data) });
   });
   await racePage.goto(`http://127.0.0.1:${server.address().port}`);
   await racePage.getByRole('heading', { name: 'Bună, export-race.' }).waitFor();
   await racePage.getByRole('button', { name: resource === 'csv' ? 'Export CSV' : 'Exportă calendar .ics', exact: true }).click();
   await requested;
   await racePage.getByRole('button', { name: 'Ieșire', exact: true }).click();
   await racePage.getByRole('button', { name: 'Conectare', exact: true }).waitFor();
   const received = racePage.waitForResponse(response => new URL(response.url()).pathname === exportPath);
   releaseResponse(); await received; await racePage.waitForTimeout(300);
   assert.equal(downloads.length, 0, `${resource} download after logout`); checks++;
   await raceContext.close();
  }
  const writeContext = await browser.newContext({ serviceWorkers: 'block' });
  const writePage = await writeContext.newPage();
  let releaseWrite, markWrite, sentWrite = false, replayedWrites = 0;
  const writeGate = new Promise(resolve => { releaseWrite = resolve; });
  const writeRequested = new Promise(resolve => { markWrite = resolve; });
  await writePage.route('https://analytics.micutu.com/**', route => route.abort());
  await writePage.route('**/api/**', async route => {
   const pathname = new URL(route.request().url()).pathname; let data = {}, status = 200;
   if (pathname === '/api/subscriptions' && route.request().method() === 'POST') {
    if (!sentWrite) { sentWrite = true; markWrite(); await writeGate; status = 401; }
    else { replayedWrites++; status = 201; data = item('replayed', 'Wrong session'); }
   } else if (pathname === '/api/auth/me') data = { username: 'write-race', accountContext: 'A'.repeat(64) };
   else if (pathname === '/api/auth/logout') status = 204;
   else if (pathname === '/api/subscriptions') data = { items: [], total: 0 };
   else if (pathname === '/api/subscriptions/summary') data = { byCurrency: [], totalSubscriptions: 0, activeSubscriptions: 0, dueThisWeek: 0 };
   else if (pathname === '/api/subscriptions/insights') data = { forecast: [], byCategory: [], upcoming: [], overdue: [], savingsOpportunities: [] };
   else if (pathname === '/api/budgets') data = [];
   await route.fulfill({ status, contentType: 'application/json', body: status === 204 ? '' : JSON.stringify(data) });
  });
  await writePage.goto(`http://127.0.0.1:${server.address().port}`);
  await writePage.getByRole('heading', { name: 'Bună, write-race.' }).waitFor();
  await writePage.getByRole('button', { name: '+ Nou', exact: true }).click();
  await writePage.getByLabel('Nume', { exact: true }).fill('Old session request');
  await writePage.getByLabel('Cost', { exact: true }).fill('10');
  await writePage.getByRole('button', { name: 'Adaugă abonament', exact: true }).click(); await writeRequested;
  await writePage.getByRole('button', { name: 'Ieșire', exact: true }).click();
  await writePage.getByRole('button', { name: 'Conectare', exact: true }).waitFor();
  const refusedWrite = writePage.waitForResponse(response => new URL(response.url()).pathname === '/api/subscriptions' && response.request().method() === 'POST');
  releaseWrite(); await refusedWrite; await writePage.waitForTimeout(300);
  assert.equal(replayedWrites, 0, 'An old 401 must not replay a mutation under a later authentication context'); checks++;
  await writeContext.close();
  console.log(`PASS: ${checks} dashboard assertions at ${widths.join("/")} px in ${themes.join("/")}, including duplicates, private details, trials, paged CSV, session-bound requests, price history, budgets, keyboard and WCAG`);
 } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
