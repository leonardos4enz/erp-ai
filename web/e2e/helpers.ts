import { test as base, expect, request, type APIRequestContext, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

export const BASE_URL = 'http://localhost:5090';
export const ADMIN = { username: 'admin', password: process.env.E2E_ADMIN_PASSWORD! };

/**
 * Cliente de API autenticado. Las cookies de sesión son `Secure`; el cliente HTTP de Playwright
 * no las reenvía sobre http://localhost, así que se toma de Set-Cookie y se envía explícitamente.
 */
// Sin estado: request.newContext hereda el storageState del proyecto (sesión de admin) si no se vacía.
const NO_STATE = { cookies: [], origins: [] };

export async function apiAs(username: string, password: string): Promise<APIRequestContext> {
  const anon = await request.newContext({ baseURL: BASE_URL, storageState: NO_STATE, extraHTTPHeaders: { 'X-ERP-Request': '1' } });
  const res = await anon.post('/api/auth/login', { data: { username, password } });
  expect(res.status(), `login de ${username}`).toBe(200);
  const cookie = /erp\.auth=[^;]+/.exec(res.headers()['set-cookie'] ?? '')![0];
  await anon.dispose();
  return request.newContext({ baseURL: BASE_URL, storageState: NO_STATE, extraHTTPHeaders: { 'X-ERP-Request': '1', Cookie: cookie } });
}

export const anonApi = (headers: Record<string, string> = { 'X-ERP-Request': '1' }) =>
  request.newContext({ baseURL: BASE_URL, storageState: NO_STATE, extraHTTPHeaders: headers });

/**
 * Aplica un plan de cambios por API (para preparar datos rápido en pruebas de UI). Como el modelador: primero la vista
 * previa, que da la huella de lo revisado (spec 021), y luego aplicar con ella.
 */
export async function applyPlan(api: APIRequestContext, plan: object) {
  const preview = await api.post('/api/model/plan/preview', { data: plan });
  expect(preview.status(), await preview.text()).toBe(200);
  const { baseHash } = await preview.json();
  const res = await api.post('/api/model/plan/apply', { data: { ...plan, baseHash } });
  expect(res.status(), await res.text()).toBe(200);
}

export async function roleId(api: APIRequestContext, name: string): Promise<number> {
  const roles = await (await api.get('/api/admin/roles')).json();
  return roles.find((r: { name: string }) => r.name === name).id;
}

/** Inicia sesión por la UI en una página sin estado previo. */
export async function uiLogin(page: Page, username: string, password: string) {
  await page.goto('/login');
  await page.getByLabel('Usuario').fill(username);
  await page.getByLabel('Contraseña').fill(password);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByRole('navigation', { name: 'Navegación principal' }).or(page.getByLabel('Navegación principal'))).toBeVisible();
}

/** Revisión automática de accesibilidad (constitución IV.3): falla con violaciones serias o críticas. */
export async function expectAccessible(page: Page) {
  // Un diálogo que aparece se mide a la mitad de su animación (opacidad < 1) y da contrastes falsos: se espera a que termine.
  await page.evaluate(() => Promise.all(document.getAnimations().filter((a) => a.effect?.getTiming().iterations !== Infinity).map((a) => a.finished.catch(() => undefined))));
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`)).toEqual([]);
}

/**
 * Prueba base: además de lo que cada prueba verifique, falla si la página lanza errores de JavaScript
 * o si la CSP bloquea algo. Los "Failed to load resource" son respuestas 4xx esperadas y se ignoran.
 */
export const test = base.extend<{ guard: void }>({
  guard: [async ({ page }, use) => {
    const problems: string[] = [];
    page.on('pageerror', (e) => problems.push(`JS: ${e.message}`));
    page.on('console', (m) => {
      if (m.type() !== 'error') return;
      const text = m.text();
      if (text.startsWith('Failed to load resource')) return;
      problems.push(`console: ${text}`);
    });
    await use();
    expect(problems, 'errores en la página o violaciones de CSP').toEqual([]);
  }, { auto: true }],
});

export { expect };

/**
 * Spec 026: el JSON de cada sección del modelador es avanzado y está plegado («Ver como JSON»). Abre el de la etiqueta
 * dada (p. ej. «Reglas (JSON)») y devuelve su área de texto.
 */
export async function openJson(page: Page, label: string) {
  const box = page.getByLabel(label);
  if (!(await box.isVisible())) await page.locator('details.json-advanced', { has: box }).locator('summary').click();
  await expect(box).toBeVisible();
  return box;
}
