// Spec 010 · Multi-empresa y multi-moneda. Corre después de inventario.spec.ts (Compras 1.6.0 e Inventario 1.4.0
// instalados, con sus datos en la empresa predeterminada). El motor se prueba con catálogos propios en su grupo del menú;
// lo que ve una persona, por la UI. Al final las empresas de la prueba se desactivan: las suites siguientes (y las
// capturas de referencia) vuelven a ver una sola empresa.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de empresas 2026';
const GROUP = 'Pruebas multiempresa';
const NORTE = { code: 'E2E-NORTE', name: 'Industrias del Norte E2E' };
const USA = { code: 'E2E-USA', name: 'Norte USA E2E' };
const users = { ana: 'e2e-me-ana', beto: 'e2e-me-beto' } as const;
type Who = keyof typeof users | 'admin';

type Row = { id: number; _rowVersion: string; [k: string]: unknown };
type Company = { id: number; code: string; name: string; baseCurrency: string; isDefault: boolean; isActive: boolean };
const company: Record<'default' | 'norte' | 'usa', Company> = { default: null!, norte: null!, usa: null! };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));
const relogin = async (w: Who) => { await api[w]?.dispose(); delete api[w]; return as(w); };

async function json(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row & { items: Row[]; total: number; title?: string; errors?: Record<string, string[]> } };
}
const post = async (w: Who, url: string, data: object) => json(await (await as(w)).post(url, { data }));
const get = async (w: Who, url: string, params?: Record<string, string | number>) => json(await (await as(w)).get(url, { params }));
const list = async (w: Who, entity: string) => (await get(w, `/api/data/${entity}`, { pageSize: 200 })).body;
const switchTo = async (w: Who, c: Company) => {
  const r = await post(w, '/api/auth/company', { companyId: c.id });
  expect(r.status, JSON.stringify(r.body)).toBe(200);
  return r.body as unknown as { company: Company; companies: Company[] };
};
const companies = async () => (await (await as('admin')).get('/api/admin/companies')).json() as Promise<Company[]>;

async function pageAs(browser: Browser, w: keyof typeof users, width = 1280): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined, viewport: { width, height: 800 } });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

/** Sin desplazamiento horizontal en la página (regla de la revisión exploratoria 009: recorrer varios anchos). */
async function noOverflow(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

test.afterAll(async () => {
  // Las suites siguientes y las capturas esperan una sola empresa activa.
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  for (const c of (await (await admin.get('/api/admin/companies')).json()) as Company[])
    if (c.code.startsWith('E2E-') && c.isActive)
      await admin.put(`/api/admin/companies/${c.id}`, { data: { name: c.name, baseCurrency: c.baseCurrency, isActive: false } });
  await admin.dispose();
});

test('preparar: empresa en dólares, catálogos de prueba, rol y usuarios', async () => {
  const admin = await as('admin');
  const all = await companies();
  company.default = all.find((c) => c.isDefault)!;
  expect(company.default).toMatchObject({ code: 'PRINCIPAL', baseCurrency: 'MXN', isActive: true });

  const usa = await admin.post('/api/admin/companies', { data: { code: 'e2e-usa', name: USA.name, baseCurrency: 'usd' } });
  expect(usa.status(), await usa.text()).toBe(200);
  company.usa = await usa.json();
  expect(company.usa).toMatchObject({ code: USA.code, baseCurrency: 'USD', isActive: true, isDefault: false });

  await applyPlan(admin, { description: 'Spec 010 · catálogos de prueba', operations: [
    { op: 'createEntity', group: GROUP, name: 'MeArticulo', label: 'Artículo ME', labelPlural: 'Artículos ME', displayField: 'Clave', fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true, unique: true },
    ] },
    { op: 'createEntity', group: GROUP, name: 'MeAlmacen', label: 'Almacén ME', labelPlural: 'Almacenes ME', displayField: 'Clave', perCompany: true, fields: [
      { name: 'Clave', label: 'Clave', type: 'text', required: true, unique: true },
      { name: 'Nombre', label: 'Nombre', type: 'text' },
    ] },
    { op: 'createEntity', group: GROUP, name: 'MeNota', label: 'Nota ME', labelPlural: 'Notas ME', displayField: 'Folio', perCompany: true, fields: [
      { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'ME-', padding: 4 },
      { name: 'Almacen', label: 'Almacén', type: 'reference', referenceEntity: 'MeAlmacen' },
      { name: 'Moneda', label: 'Moneda', type: 'text', maxLength: 3, required: true, defaultValue: 'BASECURRENCY()' },
      { name: 'Fecha', label: 'Fecha', type: 'date', required: true, defaultValue: 'TODAY()' },
      { name: 'TipoCambio', label: 'Tipo de cambio', type: 'decimal', precision: 18, scale: 4, required: true, defaultValue: 'RATE(Moneda, BASECURRENCY(), Fecha)' },
      { name: 'Total', label: 'Total', type: 'decimal' },
      { name: 'TotalBase', label: 'Total en moneda base', type: 'formula', resultType: 'decimal', formula: 'ROUND(Total * TipoCambio, 2)' },
      { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'MeNotaPartida' },
    ] },
    { op: 'createEntity', name: 'MeNotaPartida', label: 'Partida ME', labelPlural: 'Partidas ME', fields: [
      { name: 'Nota', label: 'Nota', type: 'parent', referenceEntity: 'MeNota' },
      { name: 'Articulo', label: 'Artículo', type: 'reference', referenceEntity: 'MeArticulo' },
      { name: 'Cantidad', label: 'Cantidad', type: 'decimal' },
    ] },
    { op: 'setReports', entity: 'MeNota', reports: [{ name: 'Conteo', label: 'Notas', measures: [{ fn: 'count', label: 'Notas' }] }] },
  ] });

  const perms = ['MeArticulo', 'MeAlmacen', 'MeNota'].map((entity) => ({ entity, canRead: true, canCreate: true, canUpdate: true, canDelete: true }));
  const role = await admin.post('/api/admin/roles', { data: { name: 'Operador multiempresa', description: 'E2E spec 010', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: perms } });
  expect(role.status(), await role.text()).toBe(200);
  const rid = await roleId(admin, 'Operador multiempresa');
  // Ana empieza solo con la empresa en dólares; Beto no indica empresas: recibe la predeterminada (H1 CA2).
  for (const [u, extra] of [[users.ana, { companyIds: [company.usa.id] }], [users.beto, {}]] as const) {
    const res = await admin.post('/api/admin/users', { data: { username: u, displayName: u, password: PWD, roleIds: [rid], ...extra } });
    expect(res.status(), await res.text()).toBe(201);
  }
  const beto = (await (await admin.get('/api/admin/users')).json()).find((x: { username: string }) => x.username === users.beto);
  expect(beto.companyIds).toEqual([company.default.id]);
});

test('H1 CA1: el administrador da de alta y edita empresas; la predeterminada no se desactiva', async ({ page }) => {
  await page.goto('/admin/companies');
  await expect(page.getByRole('heading', { name: 'Empresas' })).toBeVisible();
  const table = page.getByRole('table');
  await expect(table.getByRole('row', { name: /Empresa principal/ })).toContainText('Predeterminada');
  await expect(table.getByRole('row', { name: new RegExp(USA.name) })).toContainText('USD');

  await page.getByRole('button', { name: 'Nueva empresa' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/^Clave/).fill('x');
  await dialog.getByLabel(/^Nombre/).fill(NORTE.name);
  await dialog.getByLabel(/^Moneda base/).fill('p1');
  await dialog.getByRole('button', { name: 'Crear empresa' }).click();
  await expect(dialog.getByText('Usa de 2 a 20 letras sin acento')).toBeVisible();
  await expect(dialog.getByText('Usa el código ISO de 3 letras')).toBeVisible();
  await dialog.getByLabel(/^Clave/).fill('e2e-norte');
  await expect(dialog.getByLabel(/^Clave/)).toHaveValue(NORTE.code); // se escribe en mayúsculas
  await dialog.getByLabel(/^Moneda base/).fill('mxn');
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Crear empresa' }).click();
  await expect(page.getByText('Empresa creada')).toBeVisible();
  await expect(table.getByRole('row', { name: new RegExp(NORTE.name) })).toContainText('MXN');
  company.norte = (await companies()).find((c) => c.code === NORTE.code)!;

  // La clave no cambia; la predeterminada no se puede desactivar (tampoco por API).
  await table.getByRole('button', { name: `Editar ${NORTE.name}` }).click();
  await expect(dialog.getByLabel(/^Clave/)).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
  await table.getByRole('button', { name: 'Editar Empresa principal' }).click();
  await expect(dialog.getByRole('switch')).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
  const off = await (await as('admin')).put(`/api/admin/companies/${company.default.id}`, { data: { name: 'Empresa principal', baseCurrency: 'MXN', isActive: false } });
  expect(off.status()).toBe(400);
  expect((await off.json()).errors.isActive[0]).toMatch(/predeterminada no se puede desactivar/);
  const dup = await (await as('admin')).post('/api/admin/companies', { data: { code: NORTE.code, name: 'Otra', baseCurrency: 'MXN' } });
  expect((await dup.json()).errors.code[0]).toMatch(/Ya existe/);

  // Solo superusuario administra empresas; queda en la auditoría.
  expect((await (await as('beto')).post('/api/admin/companies', { data: { code: 'NOPE', name: 'x', baseCurrency: 'MXN' } })).status()).toBe(403);
  expect((await (await as('beto')).get('/api/admin/companies')).status()).toBe(403);
  const audit = await (await (await as('admin')).get('/api/audit', { params: { action: 'company.change' } })).json();
  expect(audit.items.map((a: { recordId: string }) => a.recordId)).toEqual(expect.arrayContaining([NORTE.code, USA.code]));
});

test('H1 CA2: en Usuarios se eligen sus empresas y cambiarlas cierra sus sesiones', async ({ page }) => {
  const before = await as('ana');
  expect((await before.get('/api/auth/me')).status()).toBe(200);

  await page.goto('/admin/security');
  const row = page.getByRole('row', { name: new RegExp(users.ana) });
  await expect(row).toContainText(USA.name);
  await row.getByRole('button', { name: 'Editar' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('checkbox', { name: new RegExp(USA.name) })).toBeChecked();
  await dialog.getByRole('checkbox', { name: new RegExp(NORTE.name) }).check();
  await dialog.getByRole('button', { name: 'Guardar' }).click();
  await expect(page.getByText('Usuario actualizado')).toBeVisible();
  await expect(row).toContainText(`${NORTE.name}, ${USA.name}`);

  expect((await before.get('/api/auth/me')).status()).toBe(401); // sello renovado
  delete api.ana;
  const me = (await get('ana', '/api/auth/me')).body as unknown as { company: Company; companies: Company[] };
  expect(me.companies.map((c) => c.code)).toEqual([NORTE.code, USA.code]);
  expect(me.company.code).toBe(NORTE.code); // sin elección guardada: la primera de las suyas
  const audit = await (await (await as('admin')).get('/api/audit', { params: { action: 'security.user' } })).json();
  expect([...audit.items[0].after.companyIds].sort()).toEqual([company.norte.id, company.usa.id].sort());
});

test('H1 CA3 + H2 CA1: con una empresa no hay selector; con varias se elige en la barra superior y se recuerda', async ({ browser }) => {
  const beto = await pageAs(browser, 'beto');
  await expect(beto.getByRole('banner').getByRole('button', { name: /Cambiar de empresa/ })).toHaveCount(0);
  await beto.getByRole('button', { name: 'Menú de usuario' }).click();
  await expect(beto.getByRole('menu')).toContainText('Empresa principal · MXN');
  await beto.context().close();

  const page = await pageAs(browser, 'ana');
  const switcher = page.getByRole('banner').getByRole('button', { name: /Cambiar de empresa/ });
  await expect(switcher).toHaveAccessibleName(`Empresa: ${NORTE.name}. Cambiar de empresa`);
  // Con teclado: abre en la empresa actual, flechas para moverse, Esc cierra y devuelve el foco.
  await switcher.focus();
  await page.keyboard.press('Enter');
  const menu = page.getByRole('menu', { name: 'Empresas' });
  await expect(menu.getByRole('menuitemradio', { name: new RegExp(NORTE.name) })).toBeFocused();
  await expect(menu.getByRole('menuitemradio', { name: new RegExp(NORTE.name) })).toHaveAttribute('aria-checked', 'true');
  await expectAccessible(page);
  await page.keyboard.press('Escape');
  await expect(menu).toHaveCount(0);
  await expect(switcher).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.press('ArrowDown');
  await expect(menu.getByRole('menuitemradio', { name: new RegExp(USA.name) })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(switcher).toHaveAccessibleName(`Empresa: ${USA.name}. Cambiar de empresa`);
  await expect(page).toHaveURL(/\/$/);

  // La elección se recuerda (en el servidor) al recargar y en otra sesión.
  await page.reload();
  await expect(page.getByRole('banner').getByRole('button', { name: /Cambiar de empresa/ })).toHaveAccessibleName(new RegExp(USA.name));
  expect(((await get('ana', '/api/auth/me')).body as unknown as { company: Company }).company.code).toBe(USA.code);
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await expect(page.getByRole('menu')).toContainText(`${USA.name} · USD`);
  await page.context().close();
});

const ids = { almUsa: 0, almNorte: 0, notaUsa: 0, notaNorte: 0, articulo: 0 };

test('H3 CA2 · CA3 · CA4: cada empresa ve y captura solo sus registros (también el superusuario); únicos por empresa', async () => {
  // Ana está en USA (prueba anterior).
  const art = await post('admin', '/api/data/MeArticulo', { Clave: 'TORNILLO' });
  expect(art.status).toBe(201);
  ids.articulo = art.body.id;
  const usa = await post('ana', '/api/data/MeAlmacen', { Clave: 'PRIN', Nombre: 'Principal USA' });
  expect(usa.status, JSON.stringify(usa.body)).toBe(201);
  ids.almUsa = usa.body.id;
  // La empresa no es un campo: el cliente no la elige.
  const forced = await post('ana', '/api/data/MeAlmacen', { Clave: 'OTRA', CompanyId: company.norte.id });
  expect(forced.status).toBe(400);
  expect(forced.body.errors?.CompanyId?.[0]).toBe('Campo desconocido.');

  await switchTo('ana', company.norte);
  expect((await list('ana', 'MeAlmacen')).total).toBe(0);
  expect((await get('ana', `/api/data/MeAlmacen/${ids.almUsa}`)).status).toBe(404);
  const a = await as('ana');
  expect((await a.put(`/api/data/MeAlmacen/${ids.almUsa}`, { data: { _rowVersion: usa.body._rowVersion, Nombre: 'x' } })).status()).toBe(404);
  expect((await a.delete(`/api/data/MeAlmacen/${ids.almUsa}`)).status()).toBe(404);
  expect(await (await a.get('/api/data/MeAlmacen/lookup', { params: { search: 'PRIN' } })).json()).toEqual([]);
  const cross = await post('ana', '/api/data/MeNota', { Almacen: ids.almUsa, Total: 10 });
  expect(cross.status).toBe(400);
  expect(cross.body.errors?.Almacen?.[0]).toBe('El registro seleccionado no existe.');

  // Mismo «PRIN» en otra empresa: los únicos son por empresa; repetirlo en la misma, no.
  const norte = await post('ana', '/api/data/MeAlmacen', { Clave: 'PRIN', Nombre: 'Principal Norte' });
  expect(norte.status, JSON.stringify(norte.body)).toBe(201);
  ids.almNorte = norte.body.id;
  const again = await post('ana', '/api/data/MeAlmacen', { Clave: 'PRIN' });
  expect(again.status).toBe(400);
  expect(again.body.errors?.Clave?.[0]).toMatch(/Ya existe un registro con ese valor de Clave/);
  // Un catálogo compartido sí se ve desde cualquier empresa.
  expect((await list('ana', 'MeArticulo')).items.map((x) => x.Clave)).toContain('TORNILLO');

  const nota = await post('ana', '/api/data/MeNota', { Almacen: ids.almNorte, Total: 10, Partidas: [{ Articulo: ids.articulo, Cantidad: 2 }] });
  expect(nota.status, JSON.stringify(nota.body)).toBe(201);
  ids.notaNorte = nota.body.id;
  await switchTo('ana', company.usa);
  const notaUsa = await post('ana', '/api/data/MeNota', { Almacen: ids.almUsa, Total: 20 });
  expect(notaUsa.status, JSON.stringify(notaUsa.body)).toBe(201);
  ids.notaUsa = notaUsa.body.id;
  expect((await list('ana', 'MeNota')).items.map((x) => x.id)).toEqual([ids.notaUsa]);

  // El superusuario en la predeterminada tampoco ve los registros de otras empresas.
  expect((await get('admin', `/api/data/MeNota/${ids.notaUsa}`)).status).toBe(404);
  expect((await list('admin', 'MeAlmacen')).total).toBe(0);
  // Reportes, exportación e importación también trabajan en la empresa actual.
  const report = (await get('ana', '/api/reports/MeNota/Conteo')).body as unknown as { totals: Record<string, number> };
  expect(report.totals.m0).toBe(1);
  const csv = await (await a.get('/api/data/MeAlmacen/export', { params: { format: 'csv' } })).text();
  expect(csv).toContain('Principal USA');
  expect(csv).not.toContain('Principal Norte');
  const imp = await a.post('/api/data/MeAlmacen/import', { params: { dryRun: 'false' }, data: Buffer.from('Clave;Nombre\nIMP;Importado USA\n'), headers: { 'Content-Type': 'application/octet-stream' } });
  expect(imp.status(), await imp.text()).toBe(200);
  expect((await list('ana', 'MeAlmacen')).items.map((x) => x.Clave).sort()).toEqual(['IMP', 'PRIN']);
  await switchTo('ana', company.norte);
  expect((await list('ana', 'MeAlmacen')).items.map((x) => x.Nombre)).toEqual(['Principal Norte']);
});

test('H2 CA2: solo se eligen empresas propias y activas; si la actual se desactiva, queda en otra', async () => {
  const bad = await post('ana', '/api/auth/company', { companyId: company.default.id });
  expect(bad.status).toBe(400);
  expect(bad.body.title).toMatch(/no la tienes asignada/);
  expect((await post('beto', '/api/auth/company', { companyId: company.usa.id })).status).toBe(400);

  await switchTo('ana', company.usa);
  const admin = await as('admin');
  expect((await admin.put(`/api/admin/companies/${company.usa.id}`, { data: { name: USA.name, baseCurrency: 'USD', isActive: false } })).status()).toBe(200);
  const me = (await get('ana', '/api/auth/me')).body as unknown as { company: Company; companies: Company[] };
  expect(me.company.code).toBe(NORTE.code);
  expect(me.companies.map((c) => c.code)).toEqual([NORTE.code]);
  expect((await post('ana', '/api/auth/company', { companyId: company.usa.id })).status).toBe(400);
  expect((await list('ana', 'MeNota')).items.map((x) => x.id)).toEqual([ids.notaNorte]);
  expect((await admin.put(`/api/admin/companies/${company.usa.id}`, { data: { name: USA.name, baseCurrency: 'USD', isActive: true } })).status()).toBe(200);
  const audit = await (await admin.get('/api/audit', { params: { action: 'user.company' } })).json();
  expect(audit.items[0].after).toEqual({ company: USA.code });
});

test('H4 CA1: tipos de cambio por fecha: registrar, reemplazar el del día, filtrar y eliminar', async ({ page }) => {
  await page.goto('/admin/companies');
  await page.getByRole('tab', { name: 'Tipos de cambio' }).click();
  const form = page.getByRole('region', { name: 'Registrar tipo de cambio' });
  const save = async (from: string, to: string, date: string, rate: string) => {
    await form.getByLabel(/^De/).fill(from);
    await form.getByLabel(/^A\b/).fill(to);
    await form.getByLabel(/^Fecha/).fill(date);
    await form.getByLabel(/^Tipo de cambio/).fill(rate);
    await form.getByRole('button', { name: 'Guardar' }).click();
  };
  await save('USD', 'USD', '2026-10-01', '1');
  await expect(form.getByText('Elige dos monedas distintas.')).toBeVisible();
  await save('USD', 'MXN', '2026-10-01', '18.5');
  await expect(page.getByText('Tipo de cambio guardado')).toBeVisible();
  await save('USD', 'MXN', '2026-10-01', '18.6'); // mismo día: se reemplaza
  await save('USD', 'MXN', '2026-10-15', '19.1');
  await save('EUR', 'MXN', '2026-10-01', '1'); // capturado por error
  const table = page.getByRole('table');
  await expect(table.getByRole('row', { name: /EUR → MXN/ })).toBeVisible();
  await expect(table.getByRole('row', { name: /USD → MXN/ })).toHaveCount(2);
  await expect(table.getByRole('row', { name: /USD → MXN/ }).filter({ hasText: '18.6' })).toHaveCount(1);
  await expectAccessible(page);
  // Regresión (revisión exploratoria 010, F-01): la tarjeta del formulario no tenía relleno y el texto tocaba el borde.
  expect(await form.evaluate((e) => parseFloat(getComputedStyle(e).paddingLeft))).toBeGreaterThanOrEqual(12);

  await page.getByLabel('Filtrar moneda de origen').fill('EUR');
  await expect(table.getByRole('row', { name: /USD → MXN/ })).toHaveCount(0);
  await table.getByRole('button', { name: /Eliminar EUR → MXN/ }).click();
  const confirm = page.getByRole('dialog', { name: /Eliminar tipo de cambio/ });
  await confirm.getByRole('button', { name: 'Eliminar' }).click();
  await expect(page.getByText('Tipo de cambio eliminado')).toBeVisible();
  await expect(page.getByText('Sin tipos de cambio')).toBeVisible();

  const audit = await (await (await as('admin')).get('/api/audit', { params: { action: 'rate.change', pageSize: 10 } })).json();
  expect(audit.items[0]).toMatchObject({ recordId: 'EUR/MXN/2026-10-01', after: null });
  expect(audit.items.find((x: { recordId: string; before: unknown }) => x.recordId === 'USD/MXN/2026-10-01' && x.before)).toMatchObject({ before: { rate: 18.5 }, after: { rate: 18.6 } });
  expect((await (await as('ana')).put('/api/admin/exchange-rates', { data: { from: 'USD', to: 'MXN', date: '2026-10-02', rate: 1 } })).status()).toBe(403);
});

test('H4 CA2 · CA4: el tipo de cambio se sugiere en vivo con la fecha y la moneda base de la empresa', async ({ browser }) => {
  await switchTo('ana', company.norte); // al reactivar USA (prueba anterior) volvió su elección guardada
  const page = await pageAs(browser, 'ana'); // en la empresa del Norte (MXN)
  await page.goto('/c/MeNota/new');
  await expect(page.getByLabel(/^Moneda/)).toHaveValue('MXN'); // BASECURRENCY()
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('1');
  await page.getByLabel(/^Moneda/).fill('USD');
  await page.getByLabel(/^Fecha/).fill('2026-10-05');
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('18.6');
  await page.getByLabel(/^Fecha/).fill('2026-10-20');
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('19.1');
  await page.getByLabel(/^Total$/).fill('100');
  await expect(page.getByLabel(/^Total en moneda base/)).toHaveText(/1,?910/);
  // Sin tipo de cambio para esa fecha, el campo queda vacío y se pide capturarlo.
  await page.getByLabel(/^Fecha/).fill('2026-09-01');
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('');
  await page.getByRole('button', { name: 'Crear', exact: true }).click();
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByRole('alert').filter({ hasText: 'obligatorio' }).first()).toBeVisible();
  // Capturado a mano, ya no se reemplaza al cambiar la fecha.
  await page.getByLabel(/^Tipo de cambio/).fill('20');
  await page.getByLabel(/^Fecha/).fill('2026-10-20');
  await expect(page.getByLabel(/^Total en moneda base/)).toHaveText(/2,?000/);
  await expect(page.getByLabel(/^Tipo de cambio/)).toHaveValue('20');
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Crear', exact: true }).click();
  await expect(page).toHaveURL(/\/c\/MeNota\/\d+$/);
  await page.context().close();

  // En la empresa en dólares la misma fórmula convierte al revés (tipo inverso) y sin tipo queda vacía.
  await switchTo('ana', company.usa);
  const compute = async (values: object) =>
    ((await post('ana', '/api/data/MeNota/compute', { values })).body as unknown as { values: Record<string, unknown> }).values;
  expect(await compute({ Fecha: '2026-10-20' })).toMatchObject({ Moneda: 'USD', TipoCambio: 1 });
  expect(await compute({ Moneda: 'MXN', Fecha: '2026-10-20', Total: 1910 })).toMatchObject({ TipoCambio: 0.0524, TotalBase: 100.08 });
  expect((await compute({ Moneda: 'EUR', Fecha: '2026-10-20' })).TipoCambio).toBeNull();
  await switchTo('ana', company.norte);
});

test('H4 CA3: las fórmulas guardadas usan la empresa de cada registro; en un catálogo compartido se rechaza', async () => {
  const admin = await as('admin'); // en la empresa predeterminada
  const shared = await admin.post('/api/model/plan/preview', { data: { description: 'x', operations: [
    { op: 'addField', entity: 'MeArticulo', field: { name: 'Base', label: 'Base', type: 'formula', resultType: 'text', formula: 'BASECURRENCY()' } },
  ] } });
  const preview = await shared.json();
  expect(preview.valid).toBe(false);
  expect(JSON.stringify(preview.errors)).toContain('BASECURRENCY() solo se usa en fórmulas de catálogos por empresa');

  // Agregar la fórmula recalcula los registros existentes, cada uno con su empresa (no con la de quien cambia el modelo).
  await applyPlan(admin, { description: 'Spec 010 · moneda base', operations: [
    { op: 'addField', entity: 'MeNota', field: { name: 'MonedaBase', label: 'Moneda base', type: 'formula', resultType: 'text', formula: "BASECURRENCY() & ' · ' & CURRENTCOMPANY()" } },
  ] });
  expect((await get('ana', `/api/data/MeNota/${ids.notaNorte}`)).body.MonedaBase).toBe(`MXN · ${NORTE.code}`);
  await switchTo('ana', company.usa);
  expect((await get('ana', `/api/data/MeNota/${ids.notaUsa}`)).body.MonedaBase).toBe(`USD · ${USA.code}`);
  await switchTo('ana', company.norte);
});

test('H3 CA1 · CA5: el modelador pasa un catálogo a «por empresa» (sin volver atrás) y lo existente queda en la predeterminada', async ({ page }) => {
  const admin = await as('admin');
  await applyPlan(admin, { description: 'Spec 010 · zonas', operations: [
    { op: 'createEntity', group: GROUP, name: 'MeZona', label: 'Zona ME', labelPlural: 'Zonas ME', fields: [{ name: 'Clave', label: 'Clave', type: 'text', required: true, unique: true }] },
  ] });
  for (const Clave of ['Z1', 'Z2']) expect((await post('admin', '/api/data/MeZona', { Clave })).status).toBe(201);
  await page.goto('/admin/model?edit=MeZona');
  await page.getByText('Reglas, automatizaciones, seguridad, vistas e integraciones').click();
  const sw = page.getByRole('switch', { name: 'Un registro por empresa' });
  await expect(sw).not.toBeChecked();
  await sw.check();
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/'Zona ME' pasa a un registro por empresa \(los registros existentes quedan en la empresa predeterminada\)/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  await page.goto('/admin/model?edit=MeZona'); // con «por empresa» la sección avanzada ya abre sola
  await expect(page.getByRole('switch', { name: 'Un registro por empresa' })).toBeChecked();
  await expect(page.getByRole('switch', { name: 'Un registro por empresa' })).toBeDisabled();

  expect((await list('admin', 'MeZona')).items.map((x) => x.Clave).sort()).toEqual(['Z1', 'Z2']);
  await switchTo('admin', company.norte);
  expect((await list('admin', 'MeZona')).total).toBe(0);
  expect((await post('admin', '/api/data/MeZona', { Clave: 'Z1' })).status).toBe(201); // único por empresa
  await switchTo('admin', company.default);

  const back = await (await admin.post('/api/model/plan/preview', { data: { description: 'x', operations: [{ op: 'updateEntity', entity: 'MeZona', perCompany: false }] } })).json();
  expect(back.valid).toBe(false);
  expect(JSON.stringify(back.errors)).toContain('no puede volver a ser compartido');
});

test('H3 CA6: «por empresa» viaja en el paquete de Entornos', async () => {
  const res = await (await as('admin')).post('/api/admin/environments/export/preview', {
    data: { id: 'me-prueba', name: 'Multiempresa', version: '1.0.0', entities: ['MeAlmacen', 'MeArticulo'], dashboards: [] },
  });
  expect(res.status(), await res.text()).toBe(200);
  const steps = ((await res.json()).steps as { description: string }[]).map((s) => s.description);
  expect(steps.find((s) => s.includes("'Almacén ME'"))).toMatch(/Crear catálogo 'Almacén ME' con 2 campo\(s\), un registro por empresa$/);
  expect(steps.find((s) => s.includes("'Artículo ME'"))).toMatch(/Crear catálogo 'Artículo ME' con 1 campo\(s\)$/);
});

test('H5 CA1 · CA2 · CA3 · CA4: Compras e Inventario separan las operaciones por empresa y convierten a la moneda base', async ({ page }) => {
  const admin = await as('admin');
  const entities = await (await admin.get('/api/model/entities', { params: { includeChildren: true } })).json() as { name: string; perCompany: boolean; fields: { name: string; label: string }[] }[];
  const per = entities.filter((e) => e.perCompany).map((e) => e.name);
  expect(per).toEqual(expect.arrayContaining(['Almacen', 'Requisicion', 'OrdenCompra', 'Recepcion', 'Existencia', 'MovimientoInventario', 'Traspaso']));
  for (const shared of ['Proveedor', 'Producto', 'Moneda']) expect(per).not.toContain(shared);
  expect(entities.find((e) => e.name === 'OrdenCompra')!.fields.find((f) => f.name === 'TotalMXN')!.label).toBe('Total en moneda base');

  // Lo que ya existía quedó en la empresa predeterminada (CA4).
  const ordersDefault = (await list('admin', 'OrdenCompra')).total;
  expect(ordersDefault).toBeGreaterThan(0);
  const stockDefault = JSON.stringify((await list('admin', 'Existencia')).items.map((x) => [x.Almacen, x.Producto, x.Cantidad]));

  // El gerente de Compras (de compras.spec.ts) también trabaja en el Norte: autoriza lo que el administrador crea.
  const gerente = (await (await admin.get('/api/admin/users')).json()).find((u: { username: string }) => u.username === 'e2e-gerente');
  const upd = await admin.put(`/api/admin/users/${gerente.id}`, { data: {
    displayName: gerente.displayName, isActive: true, roleIds: gerente.roleIds, unit: gerente.unit, companyIds: [company.default.id, company.norte.id],
  } });
  expect(upd.status(), await upd.text()).toBe(200);
  const g = await apiAs('e2e-gerente', 'frase larga de compras 2026');
  expect((await g.post('/api/auth/company', { data: { companyId: company.norte.id } })).status()).toBe(200);

  await switchTo('admin', company.norte);
  expect((await list('admin', 'OrdenCompra')).total).toBe(0);
  expect((await list('admin', 'Almacen')).total).toBe(0);
  const alm = await post('admin', '/api/data/Almacen', { Clave: 'ALM-01', Nombre: 'Almacén Norte' }); // misma clave que en la predeterminada
  expect(alm.status, JSON.stringify(alm.body)).toBe(201);
  const proveedor = (await get('admin', '/api/data/Proveedor', { search: 'Industrial Supply' })).body.items[0]; // compartido, en dólares
  const producto = (await get('admin', '/api/data/Producto', { search: 'ROD-6204' })).body.items[0];
  const oc = await post('admin', '/api/data/OrdenCompra', {
    Proveedor: proveedor.id, Almacen: alm.body.id, Fecha: '2026-10-20', FechaEntrega: '2026-10-30',
    Partidas: [{ Producto: producto.id, Cantidad: 10, PrecioUnitario: 5 }],
  });
  expect(oc.status, JSON.stringify(oc.body)).toBe(201);
  expect(oc.body.Moneda__label).toBe('USD');
  expect(oc.body.TipoCambio).toBe(19.1); // RATE(USD → MXN, 2026-10-20)
  expect(oc.body.TotalMXN).toBe(Math.round(Number(oc.body.Total) * 19.1 * 100) / 100);

  const step = async (entity: string, row: Row, transition: string, by: APIRequestContext = admin) => {
    const r = await json(await by.post(`/api/data/${entity}/${row.id}/transition`, { data: { transition, _rowVersion: row._rowVersion } }));
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    return r.body;
  };
  const sent = await step('OrdenCompra', await step('OrdenCompra', await step('OrdenCompra', oc.body, 'enviar'), 'autorizar', g), 'enviarProveedor');
  await g.dispose();
  const rec = await post('admin', '/api/data/Recepcion', { OrdenCompra: sent.id, Factura: 'F-ME-1', Partidas: [{ PartidaOrden: (sent.Partidas as Row[])[0].id }] });
  expect(rec.status, JSON.stringify(rec.body)).toBe(201);
  await step('Recepcion', rec.body, 'confirmar');
  const stockNorte = (await list('admin', 'Existencia')).items;
  expect(stockNorte.map((x) => [x.Almacen, x.Producto, x.Cantidad])).toEqual([[alm.body.id, producto.id, 10]]);

  // El formulario muestra la moneda base y el tipo de cambio de la orden.
  await page.goto(`/c/OrdenCompra/${oc.body.id}`);
  await expect(page.getByText('Total en moneda base').first()).toBeVisible();

  await switchTo('admin', company.default);
  expect((await get('admin', `/api/data/OrdenCompra/${oc.body.id}`)).status).toBe(404);
  expect((await list('admin', 'OrdenCompra')).total).toBe(ordersDefault);
  expect(JSON.stringify((await list('admin', 'Existencia')).items.map((x) => [x.Almacen, x.Producto, x.Cantidad]))).toBe(stockDefault);
});

test('Empresas y selector sin desbordes de 320 a 760 px', async ({ browser }) => {
  const page = await pageAs(browser, 'ana'); // se inicia sesión en escritorio y luego se recorren los anchos
  for (const width of [320, 390, 460, 600, 760]) {
    await page.setViewportSize({ width, height: 800 });
    await page.goto('/c/MeNota/new');
    await expect(page.getByRole('banner').getByRole('button', { name: /Cambiar de empresa/ })).toBeVisible();
    expect(await noOverflow(page), `formulario a ${width}px`).toBeLessThanOrEqual(0);
    // Regresión (revisión exploratoria 010, F-02): con el selector de empresa las migas de pan se encimaban en móvil.
    const clipped = await page.getByRole('navigation', { name: 'Ruta de navegación' }).evaluate((nav) =>
      [...nav.children].filter((c) => (c as HTMLElement).offsetParent !== null && c.scrollWidth > c.clientWidth + 1).map((c) => c.textContent));
    expect(clipped, `migas de pan a ${width}px`).toEqual([]);
  }
  await page.context().close();
  const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', viewport: { width: 320, height: 800 } });
  const admin = await ctx.newPage();
  for (const width of [320, 390, 460, 600, 760]) {
    await admin.setViewportSize({ width, height: 800 });
    await admin.goto('/admin/companies');
    await expect(admin.getByRole('heading', { name: 'Empresas' })).toBeVisible();
    expect(await noOverflow(admin), `empresas a ${width}px`).toBeLessThanOrEqual(0);
    await admin.getByRole('tab', { name: 'Tipos de cambio' }).click();
    await expect(admin.getByRole('button', { name: 'Guardar' })).toBeVisible();
    expect(await noOverflow(admin), `tipos de cambio a ${width}px`).toBeLessThanOrEqual(0);
  }
  await ctx.close();
});
