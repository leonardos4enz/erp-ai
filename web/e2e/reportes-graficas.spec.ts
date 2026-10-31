// Spec 039 · Reportes 2: gráficas personalizables y medidas calculadas. Catálogos propios declarados por plan (no depende
// de otro archivo): pedidos con partidas, productos y categorías, con alcance por registro para el lector. Lo que se
// prueba (gráficas, indicador, editor del modelador), por la UI; la seguridad de los saltos y las calculadas, además, en
// tests/Erp.IntegrationTests/Reports039Tests.cs.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const PWD = 'frase larga de gráficas 039';
const ROLE = 'Rg39 Lector';
const LECTOR = 'e2e-rg39-lector';
const P = 'Rg39Partida';
let admin: APIRequestContext;
test.beforeAll(async () => { admin = await apiAs(ADMIN.username, ADMIN.password); });

async function pageAs(browser: Browser, user: string, pwd: string): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, user, pwd);
  return page;
}

const month = { field: 'Pedido.Fecha', period: 'month' };
const ventas = [{ fn: 'sum', field: 'Importe', name: 'Ventas', label: 'Ventas' }, { fn: 'sum', field: 'Costo', name: 'Costo', label: 'Costo' }];

test('preparar: catálogos a dos saltos, reportes con gráficas, tablero, lector y datos', async () => {
  const role = await admin.post('/api/admin/roles', { data: { name: ROLE, description: 'E2E spec 039', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
  expect(role.status(), await role.text()).toBe(200);
  const roleId = (await role.json()).id as number;
  await applyPlan(admin, { description: 'Spec 039 · pedidos con gráficas', operations: [
    { op: 'createEntity', group: 'Gráficas 039', name: 'Rg39Categoria', label: 'Categoría G', labelPlural: 'Categorías G', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true }, { name: 'Publica', label: 'Pública', type: 'boolean' }] },
    { op: 'createEntity', group: 'Gráficas 039', name: 'Rg39Producto', label: 'Producto G', labelPlural: 'Productos G', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Categoria', label: 'Categoría', type: 'reference', referenceEntity: 'Rg39Categoria' },
      { name: 'Interna', label: 'Categoría interna', type: 'reference', referenceEntity: 'Rg39Categoria' }] },
    { op: 'createEntity', group: 'Gráficas 039', name: 'Rg39Pedido', label: 'Pedido G', labelPlural: 'Pedidos G', fields: [
      { name: 'Fecha', label: 'Fecha', type: 'date', required: true }, { name: 'Cliente', label: 'Cliente', type: 'text', required: true }] },
    { op: 'createEntity', name: P, label: 'Partida G', labelPlural: 'Partidas G', fields: [
      { name: 'Pedido', label: 'Pedido', type: 'parent', referenceEntity: 'Rg39Pedido' },
      { name: 'Producto', label: 'Producto', type: 'reference', referenceEntity: 'Rg39Producto', required: true },
      { name: 'Importe', label: 'Importe', type: 'decimal', required: true }, { name: 'Costo', label: 'Costo', type: 'decimal', required: true }] },
    { op: 'addField', entity: 'Rg39Pedido', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: P } },
    { op: 'setSecurity', entity: 'Rg39Categoria', security: { records: [{ roles: [ROLE], where: 'Publica', access: 'read' }] } },
    { op: 'setSecurity', entity: 'Rg39Producto', security: { fields: [{ field: 'Interna', read: ['Administrador'] }] } },
    { op: 'setReports', entity: P, reports: [
      { name: 'Apiladas', label: 'Ventas por mes y categoría', groupBy: [month, { field: 'Producto.Categoria.Nombre' }], measures: [{ fn: 'sum', field: 'Importe' }],
        chart: 'column', chartOptions: { stacked: true, colors: ['lima', 'violeta'], legend: true } },
      { name: 'Combinada', label: 'Ventas y costo por mes', groupBy: [month], measures: ventas, chart: 'combo', chartOptions: { measures: [0, 1], lines: [1] } },
      { name: 'Dona', label: 'Ventas por categoría', groupBy: [{ field: 'Producto.Categoria.Nombre' }], measures: [{ fn: 'sum', field: 'Importe' }], chart: 'donut', chartOptions: { values: true } },
      { name: 'Semanas', label: 'Partidas por semana', groupBy: [{ field: 'Pedido.Fecha', period: 'week' }], measures: [{ fn: 'count' }], chart: 'area' },
      { name: 'Trimestres', label: 'Ventas por trimestre', groupBy: [{ field: 'Pedido.Fecha', period: 'quarter' }], measures: [{ fn: 'sum', field: 'Importe' }], chart: 'line' },
      { name: 'Barras', label: 'Ventas y costo por producto', groupBy: [{ field: 'Producto' }], measures: ventas, chart: 'bar', chartOptions: { measures: [0, 1], values: true } },
      { name: 'Margen', label: 'Margen por mes', groupBy: [month], measures: [...ventas, { fn: 'calc', expr: 'ROUND((Ventas - Costo) / Ventas * 100, 1)', label: 'Margen %', scale: 1 }] },
      { name: 'Kpi', label: 'Ventas del mes', groupBy: [month], measures: [{ fn: 'sum', field: 'Importe' }], chart: 'kpi' },
      { name: 'Interna', label: 'Por categoría interna', groupBy: [{ field: 'Producto.Interna.Nombre' }], measures: [{ fn: 'count' }] },
    ] },
    { op: 'setDashboard', dashboard: { name: 'Rg39Tablero', label: 'Ventas 039', icon: 'chart-column', widgets: [
      { type: 'kpi', entity: P, report: 'Kpi', label: 'Ventas del mes' },
      { type: 'chart', entity: P, report: 'Apiladas', wide: true },
      { type: 'chart', entity: P, report: 'Dona' },
      { type: 'chart', entity: P, report: 'Combinada' },
      { type: 'table', entity: P, report: 'Margen', wide: true },
    ] } },
  ] });
  const perms = ['Rg39Pedido', 'Rg39Producto', 'Rg39Categoria'].map((entity) => ({ entity, canRead: true, canCreate: false, canUpdate: false, canDelete: false }));
  expect((await admin.put(`/api/admin/roles/${roleId}`, { data: { name: ROLE, description: 'E2E spec 039', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: perms } })).status()).toBe(200);
  const u = await admin.post('/api/admin/users', { data: { username: LECTOR, displayName: 'Lector 039', password: PWD, roleIds: [roleId] } });
  expect(u.status(), await u.text()).toBe(201);

  const create = async (entity: string, data: object) => {
    const res = await admin.post(`/api/data/${entity}`, { data });
    expect(res.status(), await res.text()).toBe(201);
    return (await res.json()).id as number;
  };
  const visible = await create('Rg39Categoria', { Nombre: 'Herramientas', Publica: true });
  const secreta = await create('Rg39Categoria', { Nombre: 'Reservada', Publica: false });
  const p1 = await create('Rg39Producto', { Nombre: 'Taladro', Categoria: visible, Interna: secreta });
  const p2 = await create('Rg39Producto', { Nombre: 'Prototipo', Categoria: secreta });
  for (const [Fecha, Cliente, partidas] of [
    ['2026-07-01', 'Cero', [{ Producto: p1, Importe: 0, Costo: 0 }]],
    ['2026-08-10', 'Uno', [{ Producto: p1, Importe: 100, Costo: 60 }]],
    ['2026-10-05', 'Dos', [{ Producto: p1, Importe: 200, Costo: 150 }, { Producto: p2, Importe: 50, Costo: 10 }]],
  ] as const) await create('Rg39Pedido', { Fecha, Cliente, Partidas: partidas });
});

const openReport = async (page: Page, name: string, label: string) => {
  await page.goto(`/reports/${P}/${name}`);
  await expect(page.getByRole('heading', { level: 1, name: label })).toBeVisible();
};

test('H1 CA1 · H2 CA1 · CA2 · CA3 · H3 CA1 · CA2: columnas apiladas por categoría con colores, leyenda y meses sin registros', async ({ page }) => {
  await openReport(page, 'Apiladas', 'Ventas por mes y categoría');
  // Dos series (la segunda agrupación) y septiembre en cero aunque no tenga registros.
  const chart = page.getByRole('img', { name: /Herramientas, Reservada por .* \(2 series\): 2026-07 0\.00; 2026-08 100\.00; 2026-09 0\.00; 2026-10 200\.00/ });
  await expect(chart).toBeVisible();
  const legend = page.getByRole('list', { name: 'Leyenda' });
  await expect(legend.getByRole('listitem')).toHaveText(['Herramientas', 'Reservada']);
  // Los colores declarados (lima y violeta), en el orden de las series.
  const fills = await chart.locator('g[fill]').evaluateAll((gs) => gs.map((g) => g.getAttribute('fill')));
  expect(fills).toEqual(['var(--chart-12)', 'var(--chart-7)']);
  // H3 CA3: el valor de cada mes al pasar el cursor, y la tabla debajo.
  await expect(chart.locator('title').filter({ hasText: '2026-10' })).toHaveText(/2026-10\nHerramientas: 200\.00\nReservada: 50\.00/);
  await expect(page.getByRole('table').getByRole('row', { name: /2026-10 Reservada 50\.00/ })).toBeVisible();
  await expectAccessible(page);
});

test('H1 CA1 · CA2: combinada (columnas y línea en un eje), dona con valores, área por semana, línea por trimestre y barras', async ({ page }) => {
  await openReport(page, 'Combinada', 'Ventas y costo por mes');
  const combo = page.getByRole('img', { name: /Ventas, Costo por .* \(2 series\)/ });
  await expect(combo.locator('rect.chart-mark')).not.toHaveCount(0);
  await expect(combo.locator('polyline.chart-line')).toHaveCount(1);
  await expect(page.getByRole('list', { name: 'Leyenda' }).getByRole('listitem')).toHaveText(['Ventas', 'Costo']);

  await openReport(page, 'Dona', 'Ventas por categoría');
  await expect(page.getByRole('list', { name: 'Leyenda' }).getByRole('listitem')).toHaveText(['Herramientas · 85.7 % · 300.00', 'Reservada · 14.3 % · 50.00']);

  await openReport(page, 'Semanas', 'Partidas por semana');
  // De la semana 27 a la 41, las que no tuvieron partidas valen 0.
  await expect(page.getByRole('img', { name: /Registros por .*: 2026-S27 1; 2026-S28 0; 2026-S29 0/ })).toBeVisible();
  await expect(page.getByRole('img', { name: /Registros por/ }).locator('polygon.chart-area')).toHaveCount(1);
  await expect(page.getByRole('table').getByRole('row', { name: /2026-S41 2/ })).toBeVisible();

  await openReport(page, 'Trimestres', 'Ventas por trimestre');
  await expect(page.getByRole('table').getByRole('row', { name: /2026-T4 250\.00/ })).toBeVisible();

  await openReport(page, 'Barras', 'Ventas y costo por producto');
  const bars = page.getByRole('img', { name: /Ventas, Costo por producto \(2 series\)/ });
  await expect(bars.locator('rect.chart-mark')).toHaveCount(4);
  await expect(bars.locator('text.chart-value')).toHaveCount(4); // «mostrar valores»
});

test('H4 CA1 · CA2 · CA3: indicador con el cambio contra el periodo anterior, en el reporte y en el tablero', async ({ page }) => {
  await openReport(page, 'Kpi', 'Ventas del mes');
  // Octubre contra septiembre (sin registros: 0): hay dirección, no porcentaje inventado.
  await expect(page.getByText('Importe · 2026-10')).toBeVisible();
  await expect(page.getByText('Sube vs 2026-09 (0.00)')).toBeVisible();
  await page.goto('/dashboards/Rg39Tablero');
  const kpi = page.getByRole('region', { name: 'Ventas del mes' });
  await expect(kpi).toContainText('250.00');
  await expect(kpi).toContainText('vs 2026-09');
  await expect(page.getByRole('region', { name: 'Ventas por mes y categoría' }).getByRole('img')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Ventas por categoría' }).getByRole('img')).toBeVisible();
  await expectAccessible(page);
});

test('H5 CA1 · CA3: medida calculada en cada fila y en el total, vacía sin ventas', async ({ page }) => {
  await openReport(page, 'Margen', 'Margen por mes');
  const table = page.getByRole('table');
  await expect(table.getByRole('columnheader', { name: 'Margen %' })).toBeVisible();
  await expect(table.getByRole('row', { name: /2026-07 0\.00 0\.00 —/ })).toBeVisible();
  await expect(table.getByRole('row', { name: /2026-08 100\.00 60\.00 40\.0/ })).toBeVisible();
  await expect(table.getByRole('row', { name: /2026-10 250\.00 160\.00 36\.0/ })).toBeVisible();
  await expect(table.getByRole('row', { name: /Total 350\.00 220\.00 37\.1/ })).toBeVisible();
});

test('H6 CA2 · CA3: el lector ve «(sin acceso)» a dos saltos, no ve el reporte con un campo oculto y la exportación coincide', async ({ browser }) => {
  const page = await pageAs(browser, LECTOR, PWD);
  await openReport(page, 'Dona', 'Ventas por categoría');
  const table = page.getByRole('table');
  await expect(table.getByRole('row', { name: /\(sin acceso\) 50\.00/ })).toBeVisible();
  await expect(page.getByRole('main')).not.toContainText('Reservada');
  const csv = await page.request.get(await page.getByRole('link', { name: 'CSV' }).getAttribute('href') ?? '');
  const text = await csv.text();
  expect(text).toContain('(sin acceso)');
  expect(text).not.toContain('Reservada');
  await page.goto('/reports');
  await expect(page.getByRole('link', { name: /Ventas por categoría/ })).toBeVisible();
  await expect(page.getByRole('link', { name: /Por categoría interna/ })).toHaveCount(0);
  await page.goto(`/reports/${P}/Interna`);
  await expect(page.getByRole('main').getByText('Página no encontrada')).toBeVisible();
  await page.context().close();
});

test('H6 CA1 · H1 CA1 · H3 CA1 · H5 CA1: el modelador arma un reporte a dos saltos con calculada, combinada y colores sin JSON', async ({ page }) => {
  await page.goto(`/admin/model?edit=${P}&tab=vistas`);
  await page.getByRole('button', { name: 'Agregar reporte' }).click();
  const rep = page.getByRole('region', { name: 'Reportes' });
  await rep.getByLabel('Nombre visible').fill('Margen por categoría');
  await rep.getByLabel('Identificador').fill('MargenCategoria');
  await rep.getByRole('button', { name: 'Agregar agrupación' }).click();
  await rep.getByLabel('Agrupación 1: campo').selectOption({ label: 'Producto › Categoría › Nombre' });
  await rep.getByLabel('Medida 1: cálculo').selectOption('sum');
  await rep.getByLabel('Medida 1: campo').selectOption('Importe');
  await rep.getByLabel('Medida 1: nombre para calcular (opcional)').fill('Ventas');
  await rep.getByRole('button', { name: 'Agregar medida' }).click();
  await rep.getByLabel('Medida 2: campo').selectOption('Costo');
  await rep.getByLabel('Medida 2: nombre para calcular (opcional)').fill('Costo');
  await rep.getByRole('button', { name: 'Agregar medida' }).click();
  await rep.getByLabel('Medida 3: cálculo').selectOption('calc');
  await rep.getByLabel('Medida 3: expresión').fill('Ventas - Costo');
  await rep.getByLabel('Medida 3: etiqueta (opcional)').fill('Utilidad');
  await rep.getByLabel('Medida 3: decimales').selectOption('0');
  await rep.getByLabel('Tipo de gráfica').selectOption('combo');
  const plotted = rep.getByRole('group', { name: 'Medidas que se dibujan' });
  await plotted.getByLabel(/^1\. Suma/).check();
  await plotted.getByLabel(/^3\. Calculada/).check();
  await rep.getByRole('group', { name: 'Como línea' }).getByLabel(/^3\. Calculada/).check();
  // Regresión (revisión exploratoria 039, F-03): con «Automático» la muestra enseña el color que toca (azul, el primero).
  const swatch = rep.getByRole('group', { name: 'Colores de las series' }).locator('.chart-swatch').first();
  await expect(swatch).toHaveAttribute('data-color', 'azul');
  await expect(swatch).toHaveCSS('background-color', /rgb\(/);
  expect(await swatch.evaluate((n) => getComputedStyle(n).backgroundColor)).not.toBe('rgba(0, 0, 0, 0)');
  await rep.getByLabel(/^Color de 1\./).selectOption('cian');
  await rep.getByRole('switch', { name: 'Mostrar los valores' }).check({ force: true });
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();

  const def = await (await admin.get(`/api/model/entities/${P}`)).json();
  const r = def.reports.find((x: { name: string }) => x.name === 'MargenCategoria');
  // Sin nulos ni falsos: el servidor serializa lo omitido (p. ej. «stacked: false»).
  expect(JSON.parse(JSON.stringify(r, (_k, v) => (v === null || v === false ? undefined : v)))).toEqual({
    name: 'MargenCategoria', label: 'Margen por categoría', groupBy: [{ field: 'Producto.Categoria.Nombre' }],
    measures: [{ fn: 'sum', field: 'Importe', name: 'Ventas' }, { fn: 'sum', field: 'Costo', name: 'Costo' }, { fn: 'calc', expr: 'Ventas - Costo', label: 'Utilidad', scale: 0 }],
    chart: 'combo', chartOptions: { measures: [0, 2], lines: [2], colors: ['cian'], values: true },
  });
  const run = await (await admin.get(`/api/reports/${P}/MargenCategoria`)).json();
  expect(run.rows.map((x: Record<string, unknown>) => [x.g0, x.m2])).toEqual([['Herramientas', 90], ['Reservada', 40]]);
});

test('H1 CA3: el modelador ve por qué no se puede guardar una combinación sin sentido', async ({ page }) => {
  await page.goto(`/admin/model?edit=${P}&tab=vistas`);
  await page.getByRole('button', { name: 'Agregar reporte' }).click();
  const rep = page.getByRole('region', { name: 'Reportes' });
  await rep.getByLabel('Identificador').fill('ComboSola');
  await rep.getByRole('button', { name: 'Agregar agrupación' }).click();
  await rep.getByLabel('Agrupación 1: campo').selectOption('Producto');
  await rep.getByLabel('Tipo de gráfica').selectOption('combo');
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(page.getByRole('dialog')).toContainText('la combinada necesita al menos dos medidas');
});

test('H3 CA1: capturas del tablero con los tipos nuevos (claro, oscuro y móvil)', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 1500 });
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await page.goto('/dashboards/Rg39Tablero');
    await expect(page.getByRole('region', { name: 'Ventas y costo por mes' }).getByRole('img')).toBeVisible();
    await expect(page.getByRole('main')).toHaveScreenshot(`tablero-graficas-${scheme}.png`);
  }
  await page.setViewportSize({ width: 390, height: 1800 });
  await page.goto('/dashboards/Rg39Tablero');
  await expect(page.getByRole('region', { name: 'Ventas y costo por mes' }).getByRole('img')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await expect(page.getByRole('main')).toHaveScreenshot('tablero-graficas-movil.png');
});
