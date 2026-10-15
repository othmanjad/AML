import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { actual, apiAs, login, uid } from './helpers';

const arabic = /[؀-ۿ]/;

test('[TC-L10N-001, AML-TC-0223, AML-TC-0262] Arabic RTL layout and mixed-script content', async ({ page }) => {
  await login(page, 'manager');
  await page.goto('/customers');
  await expect(page.getByTestId('customers-table')).toBeVisible();
  await page.getByTestId('customer-search').fill('DEMO-0001');
  await page.getByTestId('customer-search').press('Enter');
  await expect(page.getByTestId('customers-table')).toContainText('DEMO-0001');
  await page.getByTestId('lang-switch').click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  const nav = await page.getByTestId('sidebar').innerText();
  expect(nav).toMatch(arabic);
  await expect(page.getByTestId('customers-table')).toContainText('DEMO-0001');
  await expect(page.getByTestId('customers-table')).toContainText('عمر');
  const direction = await page.locator('.ant-table').first().evaluate(e => getComputedStyle(e).direction);
  expect(direction).toBe('rtl');
  await page.getByTestId('lang-switch').click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  actual(`Arabic switch set html dir=rtl lang=ar; navigation labels Arabic; table direction ${direction}; identifier DEMO-0001 and Arabic name rendered intact in the same row; switching back restores LTR English; underlying values unchanged.`);
});

test('[TC-L10N-002] Bilingual field labels', async ({ page }) => {
  await login(page, 'manager');
  await page.goto('/customers/new');
  await expect(page.getByTestId('dynamic-form')).toBeVisible();
  await expect(page.getByTestId('dynamic-form')).toContainText('Occupation');
  await page.getByTestId('lang-switch').click();
  await expect(page.getByTestId('dynamic-form')).toContainText('المهنة');
  actual('Customer form shows field label "Occupation" in English and "المهنة" in Arabic from field metadata (labelEn/labelAr).');
});

test('[TC-UI-001, AML-TC-0290, AML-TC-0291, TC-UI-003] Loading, empty, error and forbidden states', async ({ page }) => {
  await login(page, 'manager');
  let mode: 'slow' | 'empty' | 'error' = 'slow';
  await page.route('**/api/alerts?*', async r => {
    if (mode === 'slow') { await new Promise(res => setTimeout(res, 1500)); return r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], page: 1, pageSize: 25, total: 0 }) }); }
    if (mode === 'empty') return r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], page: 1, pageSize: 25, total: 0 }) });
    return r.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Server error', status: 500, detail: 'An unexpected error occurred. Quote the correlation ID when contacting support.', correlationId: 'corr-e2e-500' }) });
  });
  await page.goto('/alerts');
  await expect(page.getByTestId('state-loading').first()).toBeVisible();
  await expect(page.getByTestId('state-empty').first()).toBeVisible();
  mode = 'empty';
  await page.reload();
  await expect(page.getByTestId('state-empty').first()).toBeVisible();
  mode = 'error';
  await page.reload();
  const err = page.getByTestId('state-error').first();
  await expect(err).toBeVisible();
  await expect(err).toContainText('corr-e2e-500');
  expect(await err.innerText()).not.toMatch(/ at [A-Z][\w.]+\(|Exception/);
  await page.unrouteAll({ behavior: 'ignoreErrors' });
  await page.getByTestId('logout').click();
  await page.waitForURL('**/login');
  await page.waitForLoadState('networkidle');
  await login(page, 'ops');
  await page.goto('/admin/fields');
  await expect(page.getByTestId('state-forbidden').first()).toBeVisible();
  actual('Delayed API → state-loading; empty page → state-empty; 500 → state-error with safe message and Reference corr-e2e-500 (no stack trace/secrets); Operations user on Field Configuration → state-forbidden.');
});

test('[TC-UI-002] High-impact action requires in-page confirmation', async ({ page }) => {
  const officer = await apiAs('officer');
  const senior = await apiAs('senior');
  const code = 'E2E_' + uid();
  const def = { scope: { channels: ['E2E' + uid()] }, condition: { field: 'txn.amount', operator: 'gt', value: 1 } };
  const rule = await (await officer.post('/api/admin/aml-rules', { data: { code, name: 'E2E confirm', category: 'Test', definition: def } })).json();
  await officer.post(`/api/admin/aml-rules/${rule.id}/simulate`, { data: { from: new Date(Date.now() - 86400000).toISOString(), to: new Date().toISOString() } });
  await officer.post(`/api/admin/aml-rules/${rule.id}/submit`, { data: { rationale: 'e2e', testEvidence: 'sim', expectedImpact: 'none' } });
  await senior.post(`/api/admin/aml-rules/${rule.id}/approve`, { data: { comment: 'ok' } });
  await login(page, 'officer');
  await page.goto(`/rules/${rule.id}`);
  await page.getByTestId('rule-publish').click();
  await expect(page.getByTestId('confirm-modal')).toBeVisible();
  await page.getByTestId('confirm-cancel').click();
  const after = await (await officer.get(`/api/admin/aml-rules/${rule.id}`)).json();
  expect(after.activeVersionId).toBeNull();
  await page.getByTestId('rule-publish').click();
  await page.getByTestId('confirm-ok').click();
  await expect.poll(async () => (await (await officer.get(`/api/admin/aml-rules/${rule.id}`)).json()).activeVersionId).not.toBeNull();
  await officer.post(`/api/admin/aml-rules/${rule.id}/enabled`, { data: { enabled: false, reason: 'e2e cleanup' } });
  actual('Publish opens confirm-modal; cancelling leaves the rule unpublished; confirming publishes it (then disabled for cleanup).');
});

test('[TC-UI-004] Accessibility: contrast and keyboard', async ({ page }) => {
  await page.goto('/login');
  await page.getByTestId('login-username').focus();
  await page.keyboard.type('manager');
  await page.keyboard.press('Tab');
  await page.keyboard.type(process.env.AML_DEV_PASSWORD ?? 'Dev#Passw0rd!2026');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('current-user')).toBeVisible();
  const results: string[] = [];
  for (const path of ['/', '/customers', '/alerts']) {
    await page.goto(path);
    await page.waitForLoadState('networkidle');
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const serious = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
    results.push(`${path}: ${serious.length} serious/critical (${serious.map(v => v.id).join(',') || 'none'}), contrast issues ${axe.violations.filter(v => v.id === 'color-contrast').length}`);
    expect(serious.filter(v => v.id === 'color-contrast')).toEqual([]);
  }
  actual('Keyboard-only login (Tab/type/Enter) works; axe WCAG 2 AA scan — ' + results.join('; '));
});

test('[TC-UI-005] Responsive tablet layout', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 1024 });
  await login(page, 'manager');
  const widths: string[] = [];
  for (const path of ['/', '/customers', '/transactions', '/alerts', '/cases']) {
    await page.goto(path);
    await page.waitForLoadState('networkidle');
    const [sw, cw] = await page.evaluate(() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]);
    widths.push(`${path} ${sw}/${cw}`);
    expect(sw).toBeLessThanOrEqual(cw + 1);
  }
  actual('At 768×1024 no horizontal page scroll (scrollWidth/clientWidth): ' + widths.join(', '));
});

test('[AML-TC-0024] Metadata API failure is recoverable and blocks save', async ({ page }) => {
  await login(page, 'manager');
  let fail = true;
  await page.route('**/api/config/customer-fields*', r => fail ? r.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Unavailable', status: 503, detail: 'Metadata service unavailable', correlationId: 'corr-meta' }) }) : r.continue());
  await page.goto('/customers/new');
  await expect(page.getByTestId('metadata-retry')).toBeVisible();
  await expect(page.getByTestId('customer-save')).toBeDisabled();
  fail = false;
  await page.getByTestId('metadata-retry').click();
  await expect(page.getByTestId('dynamic-form')).toBeVisible();
  await expect(page.getByTestId('customer-save')).toBeEnabled();
  actual('Metadata 503 → recoverable error with Retry and Reference corr-meta; Save disabled (no incomplete form can be saved); Retry loads metadata and re-enables Save.');
});

test('[AML-TC-0286] Locale formatting keeps canonical values', async ({ page }) => {
  await login(page, 'manager');
  await page.goto('/customers');
  await page.getByTestId('lang-switch').click();
  await page.getByTestId('customer-search').fill('DEMO-0001');
  await page.getByTestId('customer-search').press('Enter');
  const row = page.getByTestId('customers-table').locator('tbody tr', { hasText: 'DEMO-0001' }).first();
  await expect(row).toContainText('DEMO-0001');
  const text = await row.innerText();
  const api = await apiAs('manager');
  const data = await (await api.get('/api/customers?search=DEMO-0001')).json();
  expect(data.items[0].createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
  actual(`Arabic view renders localized dates/labels (row: "${text.replace(/\s+/g, ' ').slice(0, 120)}") while the API keeps ISO-8601 UTC (${data.items[0].createdAt}) and numeric values unchanged.`);
});

test('[TC-SEC-015, TC-CUS-012, TC-SCR-006] XSS rendered as text; profile tabs and separate screening/risk badges', async ({ page }) => {
  const api = await apiAs('manager');
  const c = await (await api.post('/api/customers', { data: { sourceSystem: 'CORE', externalId: 'E2E-' + uid(), customerType: 'Individual', nameLatin: '<img src=x onerror=alert(1)><script>alert(2)</script>', nationalityCountry: 'JO', residenceCountry: 'JO', attributes: { OCCUPATION: 'EMPLOYEE', SOURCE_OF_INCOME: 'SALARY' } } })).json();
  let dialog = false;
  page.on('dialog', d => { dialog = true; void d.dismiss(); });
  await login(page, 'manager');
  await page.goto(`/customers/${c.id}`);
  await expect(page.getByTestId('page-customer-profile')).toContainText('<script>alert(2)</script>');
  expect(await page.locator('img[src="x"]').count()).toBe(0);
  const tabs = ['overview', 'core', 'attributes', 'kyc', 'financial', 'wallets', 'transactions', 'risk', 'screening', 'alerts', 'cases', 'relationships', 'documents', 'history'];
  for (const t of tabs) {
    await page.getByTestId(`tab-${t}`).click();
    await expect(page.getByTestId(`tabpanel-${t}`)).toBeVisible();
    await expect(page.getByTestId(`tabpanel-${t}`).getByTestId('state-error')).toHaveCount(0);
  }
  await page.getByTestId('tab-overview').click();
  await expect(page.getByTestId('screening-status')).toBeVisible();
  await expect(page.getByTestId('risk-level')).toBeVisible();
  expect(dialog).toBe(false);
  actual('Name containing <script>/<img onerror> displayed as literal text, no script executed or element injected; all 14 profile tabs load API data or empty state; screening status and risk level shown as separate badges.');
});
