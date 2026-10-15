import { APIRequestContext, Page, expect, request } from '@playwright/test';

export const PASSWORD = process.env.AML_DEV_PASSWORD ?? 'Dev#Passw0rd!2026';
export const API = process.env.AML_API_URL ?? 'http://localhost:5080';

/** Records the observed outcome; collected into the test-execution workbook as "Actual Result". */
export function actual(text: string) { console.log('ACTUAL: ' + text); }

export async function login(page: Page, user: string) {
  await page.goto('/login');
  await page.getByTestId('login-username').fill(user);
  await page.getByTestId('login-password').fill(PASSWORD);
  await page.getByTestId('login-submit').click();
  await expect(page.getByTestId('current-user')).toBeVisible();
}

export async function apiAs(user: string): Promise<APIRequestContext> {
  const ctx = await request.newContext({ baseURL: API });
  const r = await ctx.post('/api/auth/login', { data: { username: user, password: PASSWORD } });
  const { accessToken } = await r.json();
  await ctx.dispose();
  return request.newContext({ baseURL: API, extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` } });
}

export const uid = () => Math.random().toString(36).slice(2, 10).toUpperCase();
