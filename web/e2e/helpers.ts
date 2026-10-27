import { test as base, expect, request, type APIRequestContext, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Spec 028: base y puertos por carpeta (los fija run.mjs; ver instancia.mjs). Ninguna prueba fija un puerto ni la base.
const PORT_BASE = Number(process.env.E2E_PORT_BASE ?? 5090);
export const PORTS = {
  api: PORT_BASE,
  asistente: PORT_BASE + 1, // proveedor simulado de Anthropic (asistente.spec.ts)
  produccion: PORT_BASE + 2, // libre desde la 030 (la instancia sin AllowDirectChanges está en integración)
  seguridad015: PORT_BASE + 3, // instancia con aprobación de seguridad (jerarquia.spec.ts)
  seguridad021: PORT_BASE + 4, // libre desde la 030 (Hardening021Tests)
};
export const DB = process.env.E2E_DB ?? 'ErpAi_E2E';
export const BASE_URL = `http://localhost:${PORTS.api}`;
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
  await page.getByLabel('Contraseña', { exact: true }).fill(password);
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
 * Spec 030 (H5): guardias que corren en cada prueba de UI. Las lecciones que se repitieron tres veces (CLAUDE.md) ya no
 * dependen de que alguien las recuerde:
 * - `desborde`: el documento no se desplaza por un absoluto que escapa de su contenedor (003 F-16, 025 F-01, 026 F-02).
 * - `alerta`: tras un 4xx a una escritura, el motivo está en un `role="alert"` dentro de la vista (018 F-01, 021 F-01, 022 F-03).
 * - `foco`: tras cerrar un diálogo o recibir un rechazo, el foco no cae en `body` (022 F-03, 025 F-07, 026 F-01).
 * Una prueba que provoca a propósito una de estas situaciones la apaga con `test.use({ guardias: { alerta: false } })`
 * y un comentario que diga por qué.
 */
export type Guardias = { desborde: boolean; alerta: boolean; foco: boolean };

/**
 * Corre en la página antes que la SPA (los avisos y el foco se observan ahí: Playwright es más rápido que una persona y,
 * revisando desde la prueba, el siguiente paso ya habría borrado el aviso):
 * - tras un 4xx a una escritura (`fetch` no GET), el motivo debe verse en algún momento del segundo y medio siguiente en
 *   un `role="alert"`/`alertdialog` con texto, dentro de la vista y sin nada encima; y cuando aparece, el foco no debe
 *   estar en body;
 * - al cerrarse un diálogo, el foco no debe quedar en body.
 */
function vigilar(opciones: Guardias) {
  const w = window as unknown as { __guardia: string[]; __guardiaPendientes: number; __ultimo?: string };
  w.__guardia = [];
  w.__guardiaPendientes = 0;
  addEventListener('focusin', (e) => {
    const t = e.target as HTMLElement;
    w.__ultimo = `${t.tagName?.toLowerCase()}${t.textContent?.trim() ? ` «${t.textContent.trim().slice(0, 30)}»` : ''}`;
  }, true);
  const enBody = () => !document.activeElement || document.activeElement === document.body;
  const visible = (el: Element) => {
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2 || r.bottom <= 0 || r.right <= 0 || r.top >= innerHeight || r.left >= innerWidth) return false;
    const x = Math.min(Math.max(r.left + Math.min(r.width / 2, 20), 1), innerWidth - 2);
    const y = Math.min(Math.max(r.top + Math.min(r.height / 2, 10), 1), innerHeight - 2);
    const hit = document.elementFromPoint(x, y);
    return !!hit && (el.contains(hit) || hit.contains(el));
  };
  const avisos = () => [...document.querySelectorAll('[role="alert"], [role="alertdialog"]')].filter((e) => (e.textContent ?? '').trim());
  const describir = () => avisos().slice(0, 3).map((e) => {
    const r = e.getBoundingClientRect();
    return `«${(e.textContent ?? '').trim().slice(0, 50)}» en ${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}×${Math.round(r.height)}`;
  }).join(' | ');

  function rechazo(url: string, status: number) {
    // 428: el servidor pide confirmar avisos (023) y la SPA abre su diálogo; no es un rechazo que deba anunciarse.
    const pideAviso = opciones.alerta && status !== 428;
    // Revisión adversarial 030 (F-D): un aviso que ya estaba antes del rechazo, con el mismo texto, no cuenta como su motivo.
    const previos = new Map(avisos().map((e) => [e, (e.textContent ?? '').trim()]));
    const nuevo = (e: Element) => previos.get(e) !== (e.textContent ?? '').trim();
    let visto = false;
    let vistos = '';
    w.__guardiaPendientes++;
    const revisar = () => {
      if (visto) return;
      if (avisos().some((e) => nuevo(e) && visible(e))) {
        visto = true;
        // El botón que esperaba se rehabilita en un efecto posterior: el foco se mira un poco después.
        if (opciones.foco) setTimeout(() => { if (enBody()) w.__guardia.push(`foco en body tras el rechazo de ${url} (último enfocado: ${w.__ultimo ?? '—'})`); }, 150);
      } else vistos = describir() || vistos;
    };
    const mo = new MutationObserver(revisar);
    mo.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
    requestAnimationFrame(revisar);
    setTimeout(() => {
      mo.disconnect();
      revisar();
      if (!visto && pideAviso) w.__guardia.push(`4xx sin aviso visible (role="alert" dentro de la vista) tras ${url}; ${vistos ? `avisos fuera de la vista: ${vistos}` : 'ningún role="alert" con texto'}`);
      if (!visto && opciones.foco && enBody()) w.__guardia.push(`foco en body tras el rechazo de ${url}`);
      setTimeout(() => { w.__guardiaPendientes--; }, 200);
    }, 1500);
  }

  if (opciones.alerta || opciones.foco) {
    // Solo lo que la persona pidió: una escritura que sale hasta 300 ms después de un clic o de Enter/Espacio. La validación
    // en vivo (p. ej. la vista previa del modelador, 800 ms después de teclear) no es un rechazo que deba anunciarse.
    // Revisión adversarial 030 (F-E): una petición que sale mientras otra pedida por la persona sigue en curso o acaba de
    // terminar (p. ej. vista previa y luego aplicar) también cuenta como pedida; Espacio solo cuenta sobre un botón.
    let gesto = -Infinity;
    let enCurso = 0;
    addEventListener('pointerdown', () => { gesto = performance.now(); }, true);
    addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || (e.key === ' ' && e.target instanceof Element && e.target.closest('button, [role="button"]'))) gesto = performance.now();
    }, true);
    const original = window.fetch;
    window.fetch = async (...args: Parameters<typeof fetch>) => {
      const pedido = performance.now() - gesto < 300 || enCurso > 0;
      if (pedido) enCurso++;
      try {
        const res = await original(...args);
        const method = (args[1]?.method ?? (args[0] instanceof Request ? args[0].method : 'GET')).toUpperCase();
        if (pedido && res.status >= 400 && res.status < 500 && method !== 'GET') rechazo(`${method} ${new URL(res.url).pathname} → ${res.status}`, res.status);
        return res;
      } finally {
        if (pedido) { gesto = performance.now(); enCurso--; }
      }
    };
  }

  if (opciones.foco) {
    const abiertos = () => document.querySelectorAll('[role="dialog"], [role="alertdialog"], dialog[open]').length;
    let antes = 0;
    const observar = () => new MutationObserver(() => {
      const ahora = abiertos();
      if (ahora < antes) {
        const donde = location.pathname + location.search;
        w.__guardiaPendientes++;
        // El diálogo devuelve el foco a quien lo abrió en su limpieza (efecto de React): se mira un poco después.
        setTimeout(() => {
          if (enBody()) w.__guardia.push(`foco en body tras cerrar un diálogo en ${donde}`);
          w.__guardiaPendientes--;
        }, 250);
      }
      antes = ahora;
    }).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['open', 'role'] });
    if (document.documentElement) observar(); else document.addEventListener('DOMContentLoaded', observar);
  }
}

/** En la página: si el documento se desplaza y deja de hacerlo al ocultar los absolutos, uno escapó de su contenedor. */
function revisarDesborde(): string | null {
  const d = document.documentElement;
  const sobra = () => [d.scrollWidth - d.clientWidth, d.scrollHeight - d.clientHeight];
  const [w0, h0] = sobra();
  if (w0 <= 1 && h0 <= 1) return null;
  const absolutos = [...document.body.querySelectorAll<HTMLElement>('*')].filter((e) => getComputedStyle(e).position === 'absolute');
  // Los que escapan: sin ancestro posicionado (su contenedor es el documento) y fuera de la vista.
  const fuera = absolutos.filter((e) => {
    const r = e.getBoundingClientRect();
    return (!e.offsetParent || e.offsetParent === document.body) && (r.bottom > d.clientHeight + 1 || r.right > d.clientWidth + 1);
  });
  const antes = absolutos.map((e) => [e.style.getPropertyValue('display'), e.style.getPropertyPriority('display')] as const);
  absolutos.forEach((e) => e.style.setProperty('display', 'none', 'important'));
  const [w1, h1] = sobra();
  absolutos.forEach((e, i) => { if (antes[i][0]) e.style.setProperty('display', antes[i][0], antes[i][1]); else e.style.removeProperty('display'); });
  if ((w0 > 1 && w1 <= 1) || (h0 > 1 && h1 <= 1)) {
    const quien = fuera.slice(0, 3).map((e) => `<${e.tagName.toLowerCase()} class="${e.getAttribute('class') ?? ''}"> en <${e.parentElement?.tagName.toLowerCase()} class="${e.parentElement?.getAttribute('class') ?? ''}">${(e.textContent ?? '').trim().slice(0, 40)}`).join(' | ');
    return `el documento se desplaza ${Math.max(w0, 0)}×${Math.max(h0, 0)} px por un absoluto que escapa de su contenedor (${location.pathname}): ${quien}`;
  }
  return null;
}

/**
 * Prueba base: además de lo que cada prueba verifique, falla si la página lanza errores de JavaScript, si la CSP bloquea
 * algo o si se rompe una guardia (arriba). Los "Failed to load resource" son respuestas 4xx esperadas y se ignoran.
 */
export const test = base.extend<{ guard: void; guardias: Guardias }>({
  guardias: [{ desborde: true, alerta: true, foco: true }, { option: true }],
  guard: [async ({ page, guardias }, use) => {
    // Spec 034 (H7): con `run.mjs --lento` (obligatorio con --veces > 1) cada petición de la página a la API tarda al azar
    // hasta LENTO_MS: una prueba que escribe antes de que llegue la respuesta, o que lee el último aviso de la acción
    // anterior, falla en local como fallaba en CI (R-030-5).
    if (process.env.E2E_LENTO) await page.route('**/api/**', async (route) => {
      await new Promise((r) => setTimeout(r, Math.random() * LENTO_MS));
      await route.fallback();
    });
    const problems: string[] = [];
    page.on('pageerror', (e) => problems.push(`JS: ${e.message}`));
    page.on('console', (m) => {
      if (m.type() !== 'error') return;
      const text = m.text();
      if (text.startsWith('Failed to load resource')) return;
      problems.push(`console: ${text}`);
    });
    if (guardias.alerta || guardias.foco) await page.addInitScript(vigilar, guardias);
    await use();
    if (!page.isClosed() && page.url().startsWith('http')) {
      if (guardias.alerta || guardias.foco) {
        // Lo que la página sigue observando (un rechazo de hace menos de segundo y medio, un diálogo recién cerrado).
        const pendientes = () => page.evaluate(() => (window as unknown as { __guardiaPendientes?: number }).__guardiaPendientes ?? 0).catch(() => 0);
        for (let i = 0; i < 20 && (await pendientes()) > 0; i++) await page.waitForTimeout(100);
        problems.push(...await page.evaluate(() => (window as unknown as { __guardia?: string[] }).__guardia ?? []).catch(() => []));
      }
      if (guardias.desborde) {
        const d = await page.evaluate(revisarDesborde).catch(() => null);
        if (d) problems.push(d);
      }
    }
    expect(problems, 'errores en la página, violaciones de CSP o guardias de la suite (helpers.ts)').toEqual([]);
  }, { auto: true }],
});

export { expect };

/** Spec 034 (H7): latencia máxima del modo lento (la carrera de `multiempresa › H4 CA1` se reprodujo con 700 ms). */
export const LENTO_MS = 700;

/**
 * Spec 034 (H7): la respuesta que provoca una acción. `respuesta` es la espera ya registrada (`page.waitForResponse(...)`,
 * que se evalúa antes de actuar): así no hay carrera entre actuar y empezar a esperar.
 *   const res = await tras(page.waitForResponse((r) => r.url().endsWith('/rates')), () => boton.click());
 */
export async function tras<R>(respuesta: Promise<R>, accion: () => Promise<unknown>): Promise<R> {
  const [r] = await Promise.all([respuesta, accion()]);
  return r;
}

/**
 * Spec 034 (H7): el aviso (`role="status"` o `"alert"`) que deja **esta** acción. Los que ya estaban (de la acción
 * anterior, aún visibles) no cuentan: con `getByText(...).last()` la prueba pasaba con el aviso viejo (033, integraciones).
 */
export async function expectToast(page: Page, texto: string | RegExp, accion: () => Promise<unknown>) {
  // Por atributo y no por rol: se marcan los avisos presentes para distinguirlos de los que lleguen.
  await page.evaluate(() => document.querySelectorAll('[role="status"], [role="alert"]').forEach((e) => e.setAttribute('data-e2e-previo', '')));
  await accion();
  const nuevo = page.locator('[role="status"]:not([data-e2e-previo]), [role="alert"]:not([data-e2e-previo])').filter({ hasText: texto }).first();
  await expect(nuevo).toBeVisible();
  return nuevo;
}

/**
 * Spec 034 (H7): lo que recibe un servidor simulado de la prueba (proveedor de IA, receptor de webhooks), en orden. En
 * lugar de leer el último (`.at(-1)`, que puede ser el de la acción anterior si la nueva aún no llega): `marca()` antes de
 * actuar y `siguiente(cond)` después, que espera con `expect.poll` el primero que llegue tras la marca y la avanza.
 * La guardia de `src/__tests__/e2eGuardia.test.ts` prohíbe `.at(-1)` sobre un receptor.
 */
export function receptor<T>() {
  const items: T[] = [];
  let desde = 0;
  return {
    push: (item: T) => { items.push(item); },
    get length() { return items.length; },
    todos: (): readonly T[] => items,
    marca: () => { desde = items.length; },
    async siguiente(cond: (item: T) => boolean = () => true, mensaje = 'el receptor no recibió lo esperado tras la marca'): Promise<T> {
      let hallado = -1;
      await expect.poll(() => (hallado = items.findIndex((x, i) => i >= desde && cond(x))), { message: mensaje, timeout: 15_000 }).toBeGreaterThanOrEqual(0);
      desde = hallado + 1;
      return items[hallado];
    },
  };
}

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
