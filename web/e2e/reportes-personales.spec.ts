// Spec 041 · Reportes personales y exploración al vuelo. Catálogos propios declarados por plan (no depende de otro
// archivo): pedidos con zona, región, monto y un campo interno oculto; alcance por registro para el lector (no ve la zona
// «Privada»). Lo que se prueba, por la UI como el lector (sin permiso de modelador); la concurrencia, la bitácora y las
// fallas parciales, en tests/Erp.IntegrationTests/PersonalReportsTests.cs.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, expectToast, test, tras, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
// La página de cada prueba (con las guardias de helpers.ts) entra como el lector, no como el administrador.
test.use({ storageState: { cookies: [], origins: [] } });

const PWD = 'frase larga de reportes 041';
const ROLE = 'Rp41 Lector';
const LECTOR = 'e2e-rp41-lector';
const OTRO = 'e2e-rp41-otro';
const E = 'Rp41Pedido';
let admin: APIRequestContext;
test.beforeAll(async () => { admin = await apiAs(ADMIN.username, ADMIN.password); });

async function pageAs(browser: Browser, user: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  await uiLogin(page, user, PWD);
  return page;
}

const login = (page: Page) => uiLogin(page, LECTOR, PWD);

const security = (hidden: string[]) => ({ op: 'setSecurity', entity: E, security: {
  fields: hidden.map((field) => ({ field, read: ['Administrador'] })),
  records: [{ roles: [ROLE], where: "Zona <> 'Privada'", access: 'read' }] } });

test('preparar: pedidos con zona, un campo interno, reportes del modelo, lector y datos', async () => {
  const role = await admin.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 041', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
  expect(role.status(), await role.text()).toBe(200);
  const roleId = (await role.json()).id as number;
  await applyPlan(admin, { description: 'Spec 041 · reportes personales', operations: [
    { op: 'createEntity', group: 'Reportes 041', name: E, label: 'Pedido R', labelPlural: 'Pedidos R', fields: [
      { name: 'Fecha', label: 'Fecha', type: 'date', required: true },
      { name: 'Zona', label: 'Zona', type: 'text', required: true },
      { name: 'Region', label: 'Región', type: 'text' },
      { name: 'Monto', label: 'Monto', type: 'decimal', required: true },
      { name: 'Interno', label: 'Interno', type: 'text' }] },
    { op: 'createEntity', group: 'Reportes 041', name: 'Rp41Secreto', label: 'Secreto R', labelPlural: 'Secretos R', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text' }] },
    security(['Interno']),
    { op: 'setReports', entity: E, reports: [
      { name: 'PorZona', label: 'Ventas por zona', groupBy: [{ field: 'Zona' }], measures: [{ fn: 'sum', field: 'Monto', label: 'Ventas' }], chart: 'bar' },
      { name: 'Grandes', label: 'Pedidos grandes', filter: 'Monto >= 100', groupBy: [{ field: 'Zona' }], measures: [{ fn: 'count' }] }] },
  ] });
  const perms = [{ entity: E, canRead: true, canCreate: false, canUpdate: false, canDelete: false }];
  expect((await admin.put(`/api/admin/roles/${roleId}`, { data: { name: ROLE, description: 'E2E spec 041', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: perms } })).status()).toBe(200);
  for (const [username, displayName] of [[LECTOR, 'Lector 041'], [OTRO, 'Otro 041']]) {
    const u = await admin.post('/api/admin/users', { data: { username, displayName, password: PWD, roleIds: [roleId] } });
    expect(u.status(), await u.text()).toBe(201);
  }
  for (const [Fecha, Zona, Region, Monto, Interno] of [
    ['2026-08-10', 'Norte', 'Uno', 120, 'nota interna 041'], ['2026-09-05', 'Sur', 'Uno', 80, null], ['2026-10-01', 'Norte', 'Dos', 40, null],
    ['2026-10-20', 'Sur', 'Dos', 260, null], ['2026-10-25', 'Privada', 'Dos', 5000, null],
  ] as const) {
    const res = await admin.post(`/api/data/${E}`, { data: { Fecha, Zona, Region, Monto, Interno } });
    expect(res.status(), await res.text()).toBe(201);
  }
});

const table = (page: Page) => page.getByRole('region', { name: 'Datos del reporte' }).getByRole('table');
// El texto de cada celda (no `innerText`: el encabezado va en mayúsculas por CSS), una fila por cadena.
const result = (page: Page) => table(page).getByRole('row').evaluateAll((rows) =>
  rows.map((r) => [...(r as HTMLTableRowElement).cells].map((c) => (c.textContent ?? '').trim()).filter(Boolean).join(' ')));
const explore = (page: Page) => page.waitForResponse((r) => r.url().endsWith('/api/my-reports/explore') && r.request().method() === 'POST');

test('H1 CA1 · H4 CA1: desde un reporte del modelo, «Explorar» cambia agrupación y gráfica sin tocar el reporte', async ({ page }) => {
  await login(page);
  await page.goto(`/reports/${E}/PorZona`);
  await expect(page.getByRole('heading', { level: 1, name: 'Ventas por zona' })).toBeVisible();
  await tras(explore(page), () => page.getByRole('link', { name: 'Explorar' }).click());
  await expect(page.getByRole('heading', { level: 1, name: 'Explorar: Ventas por zona' })).toBeVisible();
  // La misma definición y las mismas cifras; el lector no ve la zona Privada.
  await expect.poll(() => result(page)).toEqual(['Zona Ventas', 'Norte 160.00', 'Sur 340.00', 'Total 500.00']);
  await expect(page.getByLabel('Agrupación 1: campo')).toHaveValue('Zona');
  await expect(page.getByLabel('Tipo de gráfica')).toHaveValue('bar');
  // Otra agrupación y otra gráfica: se recalcula sin guardar.
  await tras(explore(page), async () => {
    await page.getByLabel('Agrupación 1: campo').selectOption({ label: 'Fecha' });
    await page.getByLabel('Agrupación 1: periodo').selectOption({ label: 'Por trimestre' });
  });
  await expect.poll(() => result(page)).toEqual(['Fecha Ventas', '2026-T3 200.00', '2026-T4 300.00', 'Total 500.00']);
  await tras(explore(page), () => page.getByLabel('Tipo de gráfica').selectOption({ label: 'Línea' }));
  await expect(page.getByRole('region', { name: 'Gráfica' }).getByRole('img')).toBeVisible();
  await expect(page.getByText('Privada')).toHaveCount(0);
  await expectAccessible(page);
  // El reporte del modelo sigue igual.
  await page.goto(`/reports/${E}/PorZona`);
  await expect(page.getByRole('table').getByRole('row', { name: /Norte 160\.00/ })).toBeVisible();
});

test('H1 CA2: explorar un reporte con filtro fijo lo dice y no lo copia', async ({ page }) => {
  await login(page);
  await tras(explore(page), () => page.goto(`/explore?from=${encodeURIComponent(`${E}/Grandes`)}`));
  await expect(page.getByRole('status').filter({ hasText: 'El filtro fijo del reporte original no se aplica al explorar' })).toBeVisible();
  // Sin el filtro (Monto >= 100) cuenta también los pedidos chicos.
  await expect.poll(() => result(page)).toEqual(['Zona Registros', 'Norte 2', 'Sur 2', 'Total 4']);
});

test('H1 CA3: una combinación sin sentido dice qué cambiar y no pierde lo armado', async ({ page }) => {
  await login(page);
  await tras(explore(page), () => page.goto(`/explore?from=${encodeURIComponent(`${E}/PorZona`)}`));
  await page.getByRole('button', { name: 'Agregar agrupación' }).click();
  await page.getByLabel('Agrupación 2: campo').selectOption({ label: 'Región' });
  const res = await tras(explore(page), () => page.getByLabel('Tipo de gráfica').selectOption({ label: 'Pastel' }));
  expect(res.status()).toBe(400);
  await expect(page.getByRole('alert')).toContainText('Un pastel o una dona necesita exactamente una agrupación.');
  await expect(page.getByLabel('Agrupación 2: campo')).toHaveValue('Region');
  // Al quitar la segunda agrupación vuelve a calcular.
  await tras(explore(page), () => page.getByRole('button', { name: 'Quitar agrupación 2' }).click());
  await expect.poll(() => result(page)).toEqual(['Zona Ventas', 'Norte 160.00', 'Sur 340.00', 'Total 500.00']);
});

test('H2 CA1 · CA2 · CA3 · H4 CA2: armar uno desde cero con lo que ve, medidas calculadas y filtros', async ({ page }) => {
  await login(page);
  await page.goto('/reports');
  await page.getByRole('link', { name: 'Nuevo reporte' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Nuevo reporte' })).toBeVisible();
  // Revisión exploratoria 041 (F-04): la miga lleva de vuelta a Reportes (antes decía solo «Inicio»).
  await expect(page.getByRole('navigation', { name: 'Ruta de navegación' })).toContainText('Reportes');
  // Solo los catálogos que lee (se espera la lista: con --lento las opciones llegan después que la pantalla).
  await expect(page.getByLabel('Catálogo').locator('option', { hasText: 'Pedidos R' })).toHaveCount(1);
  const catalogs = await page.getByLabel('Catálogo').locator('option').allInnerTexts();
  expect(catalogs).toContain('Pedidos R');
  expect(catalogs).not.toContain('Secretos R');
  await tras(explore(page), () => page.getByLabel('Catálogo').selectOption({ label: 'Pedidos R' }));
  await expect.poll(() => result(page)).toEqual(['Registros', '4']);
  // Solo los campos que ve (el interno no se ofrece).
  await page.getByRole('button', { name: 'Agregar agrupación' }).click();
  await expect(page.getByLabel('Agrupación 1: campo').locator('option', { hasText: 'Zona' })).toHaveCount(1);
  const fields = await page.getByLabel('Agrupación 1: campo').locator('option').allInnerTexts();
  expect(fields).toEqual(expect.arrayContaining(['Zona', 'Región', 'Fecha', 'Monto']));
  expect(fields).not.toContain('Interno');
  await page.getByLabel('Agrupación 1: campo').selectOption({ label: 'Zona' });
  // Revisión exploratoria 041 (F-01): sin periodo, el campo ocupa la fila (antes, un tercio: unos 115 px).
  expect((await page.getByLabel('Agrupación 1: campo').boundingBox())!.width).toBeGreaterThan(200);
  // Revisión exploratoria 041 (F-02): el texto de ejemplo del nombre cabe en su campo (antes se cortaba).
  expect(await page.getByLabel('Medida 1: nombre para calcular').evaluate((el) => {
    const input = el as HTMLInputElement;
    const st = getComputedStyle(input);
    const ctx = document.createElement('canvas').getContext('2d')!;
    ctx.font = st.font;
    return ctx.measureText(input.placeholder).width <= input.clientWidth - parseFloat(st.paddingLeft) - parseFloat(st.paddingRight);
  })).toBe(true);
  // Conteo con nombre, suma con nombre y una calculada sobre las dos.
  await page.getByLabel('Medida 1: nombre para calcular').fill('Pedidos');
  await page.getByRole('button', { name: 'Agregar medida' }).click();
  await page.getByLabel('Medida 2: campo').selectOption({ label: 'Monto' });
  await page.getByLabel('Medida 2: nombre para calcular').fill('Ventas');
  await page.getByRole('button', { name: 'Agregar medida' }).click();
  await page.getByLabel('Medida 3: cálculo').selectOption({ label: 'Calculada' });
  await page.getByLabel('Medida 3: etiqueta').fill('Ticket');
  await tras(explore(page), () => page.getByLabel('Medida 3: expresión').fill('ROUND(Ventas / Pedidos, 2)'));
  await expect.poll(() => result(page)).toEqual(['Zona Registros Monto Ticket', 'Norte 2 160.00 80.00', 'Sur 2 340.00 170.00', 'Total 4 500.00 125.00']);
  await page.getByLabel('Tipo de gráfica').selectOption({ label: 'Columnas' });
  await page.getByRole('checkbox', { name: '2. Suma · Monto' }).check();
  await expect(page.getByRole('region', { name: 'Gráfica' }).getByRole('img')).toBeVisible();
  // Filtros con el diálogo del listado.
  await page.getByRole('button', { name: /^Filtros/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Filtros' });
  await dialog.getByRole('button', { name: 'Agregar condición' }).click();
  await dialog.getByLabel('Campo 1', { exact: true }).selectOption({ label: 'Monto' });
  await dialog.getByLabel('Condición 1', { exact: true }).selectOption({ label: 'mayor que' });
  await dialog.getByLabel('Valor 1', { exact: true }).fill('50');
  await tras(explore(page), () => dialog.getByRole('button', { name: 'Aplicar' }).click());
  await expect.poll(() => result(page)).toEqual(['Zona Registros Monto Ticket', 'Norte 1 120.00 120.00', 'Sur 2 340.00 170.00', 'Total 3 460.00 153.33']);
  // H4 CA2: por la API, lo oculto responde igual que lo inexistente.
  const lector = await apiAs(LECTOR, PWD);
  const ask = async (field: string) => (await lector.post('/api/my-reports/explore', { data: { definition: { entity: E, groupBy: [{ field }], measures: [{ fn: 'count' }] } } }));
  const [hidden, missing] = [await ask('Interno'), await ask('Nada')];
  expect([hidden.status(), missing.status()]).toEqual([400, 400]);
  expect((await hidden.json()).errors.report).toEqual(["'Interno' no existe o no puedes verlo."]);
  expect((await missing.json()).errors.report).toEqual(["'Nada' no existe o no puedes verlo."]);
  await expectAccessible(page);
});

let savedId = 0;
/** El reporte guardado en H3 CA1, por su nombre (tras una falla el proceso se reinicia y la variable vuelve a 0). */
async function saved(): Promise<number> {
  if (savedId) return savedId;
  const list = (await (await (await apiAs(LECTOR, PWD)).get('/api/my-reports')).json()) as { id: number; label: string }[];
  savedId = list.find((r) => r.label === 'Mis zonas 041')?.id ?? 0;
  expect(savedId, 'el reporte «Mis zonas 041» de H3 CA1').toBeGreaterThan(0);
  return savedId;
}
test('H3 CA1 · CA4: guardar como mío, verlo en «Mis reportes» (solo su dueño) y en la bitácora', async ({ page, browser }) => {
  await login(page);
  await tras(explore(page), () => page.goto(`/explore?from=${encodeURIComponent(`${E}/PorZona`)}`));
  await page.getByRole('button', { name: 'Guardar como mío' }).click();
  const dialog = page.getByRole('dialog', { name: 'Guardar como mío' });
  // Revisión exploratoria 041 (F-06): el diálogo enfoca el nombre (antes, «Cerrar»).
  await expect(dialog.getByLabel('Nombre del reporte')).toBeFocused();
  await dialog.getByLabel('Nombre del reporte').fill('Mis zonas 041');
  await expectToast(page, 'Reporte guardado', () => dialog.getByLabel('Nombre del reporte').press('Enter'));
  await expect(page).toHaveURL(/\/explore\/\d+$/);
  // Revisión exploratoria 041 (F-05): el foco sigue en «Guardar» (antes caía en el contenido principal).
  await expect(page.getByRole('button', { name: 'Guardar', exact: true })).toBeFocused();
  savedId = Number(/\/explore\/(\d+)$/.exec(page.url())![1]);
  await expect(page.getByRole('heading', { level: 1, name: 'Mis zonas 041' })).toBeVisible();
  await page.goto('/reports');
  const mine = page.getByRole('region', { name: 'Mis reportes' });
  await expect(mine.getByRole('link', { name: /Mis zonas 041/ })).toBeVisible();
  await mine.getByRole('link', { name: /Mis zonas 041/ }).click();
  await expect.poll(() => result(page)).toEqual(['Zona Ventas', 'Norte 160.00', 'Sur 340.00', 'Total 500.00']);
  // Otro usuario no lo ve: ni en su lista ni por su dirección.
  const other = await pageAs(browser, OTRO);
  await other.goto('/reports');
  await expect(other.getByRole('heading', { level: 1, name: 'Reportes' })).toBeVisible();
  await expect(other.getByText('Mis zonas 041')).toHaveCount(0);
  await other.goto(`/explore/${savedId}`);
  await expect(other.getByText('Página no encontrada')).toBeVisible();
  await other.context().close();
  // La bitácora tiene el alta.
  const audit = await (await admin.get('/api/audit', { params: { entity: E, pageSize: '100' } })).json();
  expect(audit.items.some((x: { action: string; recordId: string }) => x.action === 'report.personal' && x.recordId === String(savedId))).toBe(true);
});

test('H3 CA2: si otra pestaña lo cambió antes, guardar responde 409 y no pisa el cambio', async ({ page }) => {
  await login(page);
  const a = page;
  const b = await a.context().newPage();
  const id = await saved();
  for (const p of [a, b]) await tras(explore(p), () => p.goto(`/explore/${id}`));
  await tras(explore(a), () => a.getByLabel('Tipo de gráfica').selectOption({ label: 'Columnas' }));
  await expectToast(a, 'Cambios guardados', () => a.getByRole('button', { name: 'Guardar', exact: true }).click());
  await tras(explore(b), () => b.getByLabel('Tipo de gráfica').selectOption({ label: 'Pastel' }));
  const res = await tras(b.waitForResponse((r) => r.url().includes(`/api/my-reports/${id}`) && r.request().method() === 'PUT'),
    () => b.getByRole('button', { name: 'Guardar', exact: true }).click());
  expect(res.status()).toBe(409);
  await expect(b.getByRole('alert')).toContainText('Otra pestaña o sesión cambió este reporte');
  await tras(explore(b), () => b.getByRole('button', { name: 'Volver a abrir' }).click());
  await expect(b.getByLabel('Tipo de gráfica')).toHaveValue('column');
  await b.close();
});

test('H3 CA3: «Guardar como» crea otro y el nombre no se repite', async ({ page }) => {
  await login(page);
  const id = await saved();
  await tras(explore(page), () => page.goto(`/explore/${id}`));
  await page.getByRole('button', { name: 'Guardar como', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Guardar una copia' });
  await dialog.getByLabel('Nombre del reporte').fill('Mis zonas 041');
  const res = await tras(page.waitForResponse((r) => r.url().endsWith('/api/my-reports') && r.request().method() === 'POST'),
    () => dialog.getByRole('button', { name: 'Guardar' }).click());
  expect(res.status()).toBe(400);
  await expect(dialog.getByText('Ya tienes un reporte con ese nombre.')).toBeVisible();
  await dialog.getByLabel('Nombre del reporte').fill('Mis zonas 041 (copia)');
  await expectToast(page, 'Reporte guardado', () => dialog.getByRole('button', { name: 'Guardar' }).click());
  await expect(page).not.toHaveURL(new RegExp(`/explore/${id}$`));
  await expect(page.getByRole('heading', { level: 1, name: 'Mis zonas 041 (copia)' })).toBeVisible();
  // Regresión (revisión adversarial 041, F-G): pasar a explorar un reporte del modelo sin volver a montar la pantalla no
  // deja «Guardar» apuntando al reporte guardado (lo habría sobrescrito con el del modelo).
  await tras(explore(page), () => page.evaluate((to) => { history.pushState({}, '', to); dispatchEvent(new PopStateEvent('popstate')); },
    `/explore?from=${encodeURIComponent(`${E}/Grandes`)}`));
  await expect(page.getByRole('heading', { level: 1, name: 'Explorar: Pedidos grandes' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar como mío' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guardar', exact: true })).toHaveCount(0);
  await page.goBack();
  await expect(page.getByRole('heading', { level: 1, name: 'Mis zonas 041 (copia)' })).toBeVisible();
  // Borrar la copia, con confirmación.
  await page.getByRole('button', { name: 'Borrar el reporte' }).click();
  await expectToast(page, 'Reporte borrado', () => page.getByRole('dialog', { name: 'Borrar el reporte' }).getByRole('button', { name: 'Borrar' }).click());
  await expect(page).toHaveURL(/\/reports$/);
  await expect(page.getByRole('region', { name: 'Mis reportes' }).getByRole('link')).toHaveCount(1);
});

test('H4 CA3: si deja de ver un campo que su reporte usa, el reporte deja de correr y lo dice', async ({ page }) => {
  const lector = await apiAs(LECTOR, PWD);
  const created = await lector.post('/api/my-reports', { data: { label: 'Por región 041', definition: { entity: E, groupBy: [{ field: 'Region' }], measures: [{ fn: 'sum', field: 'Monto' }] } } });
  expect(created.status(), await created.text()).toBe(201);
  const id = (await created.json()).id as number;
  await applyPlan(admin, { description: 'Spec 041 · ocultar región', operations: [security(['Interno', 'Region'])] });
  try {
    await login(page);
    await page.goto('/reports');
    await expect(page.getByRole('region', { name: 'Mis reportes' }).getByRole('link', { name: /Por región 041/ })).toContainText('No disponible');
    await page.goto(`/explore/${id}`);
    await expect(page.getByRole('status').filter({ hasText: 'Este reporte usa algo que ya no puedes ver' })).toContainText("'Region' no existe o no puedes verlo.");
    await expect(page.getByText('Uno', { exact: true })).toHaveCount(0);
    } finally {
    await applyPlan(admin, { description: 'Spec 041 · región visible', operations: [security(['Interno'])] });
  }
});

test('H4 CA4: los topes de la 039/040 valen para lo explorado', async () => {
  const lector = await apiAs(LECTOR, PWD);
  const run = (definition: object) => lector.post('/api/my-reports/explore', { data: { definition: { entity: E, measures: [{ fn: 'count' }], ...definition } } });
  expect((await (await run({ limit: 5000 })).json()).errors.report).toEqual(['«limit» va de 1 a 1000.']);
  const tooMany = await run({ measures: Array.from({ length: 7 }, () => ({ fn: 'count' })) });
  expect((await tooMany.json()).errors.report).toContain('Indica de 1 a 6 medidas.');
  const groups = await run({ groupBy: [{ field: 'Zona' }, { field: 'Region' }, { field: 'Fecha', period: 'month' }] });
  expect((await groups.json()).errors.report).toContain('Máximo 2 agrupaciones.');
});

test('H5 CA1: exportar a CSV lo que se ve en el explorador', async ({ page }) => {
  await login(page);
  const id = await saved();
  await tras(explore(page), () => page.goto(`/explore/${id}`));
  await expect.poll(() => result(page)).toEqual(['Zona Ventas', 'Norte 160.00', 'Sur 340.00', 'Total 500.00']);
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'CSV' }).click()]);
  const text = (await (await download.createReadStream()).toArray()).map((b) => b.toString()).join('');
  expect(text.replace(/^﻿/, '').trim().split(/\r?\n/)).toEqual(['Zona,Ventas', 'Norte,160.00', 'Sur,340.00', 'Total,500.00']);
});

// Capturas: el explorador con un reporte guardado (datos propios del archivo), en claro, oscuro y móvil.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 900 }], ['dark', 'dark', { width: 1280, height: 900 }],
  ['movil', 'light', { width: 390, height: 844 }]] as const) {
  test(`visual: explorador de reportes en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await login(page);
    await page.setViewportSize(viewport);
    const id = await saved();
    await tras(explore(page), () => page.goto(`/explore/${id}`));
    await expect.poll(() => result(page)).toEqual(['Zona Ventas', 'Norte 160.00', 'Sur 340.00', 'Total 500.00']);
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    await expect(page.getByRole('main')).toHaveScreenshot(`explorador-${name}.png`);
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
    });
}
