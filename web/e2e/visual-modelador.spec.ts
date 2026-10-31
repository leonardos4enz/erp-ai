// Spec 026: modelador por módulos y sin JSON — catálogos agrupados por módulo, origen de cada elemento (paquete o local),
// choques al actualizar un módulo, editor de expresiones y editores visuales de las ocho secciones con el JSON como
// opción avanzada plegada. Corre después de valuacion-* (orden alfabético): Compras, Inventario, Contabilidad y Costeo
// ya están instalados.
import { execFileSync } from 'node:child_process';
import type { APIRequestContext, Locator, Page } from '@playwright/test';
import { ADMIN, DB, apiAs, applyPlan, expect, expectAccessible, openJson, test } from './helpers';

const PWD = 'frase larga del modelador 2026';
// Sufijo por corrida: la prueba se puede repetir sobre la misma base sin chocar con lo que creó antes.
// Los workers de una corrida comparten proceso padre (el runner); otra corrida tiene otro.
const RUN = `r${process.ppid.toString(36)}`;
const LECTOR = `e2e-vm-lector-${RUN}`;
const APP = `VmPruebas${RUN}`;
const ZONA = `VmZona${RUN}`, CLIENTE = `VmCliente${RUN}`, NOTA = `VmNota${RUN}`, SERVICIO = `VmServicio${RUN}`, LECTOR_ROLE = `VmLector${RUN}`;
let admin: APIRequestContext;
// Tras una falla Playwright levanta otro worker: el cliente se vuelve a crear en cada uno.
test.beforeAll(async () => { admin = await apiAs(ADMIN.username, ADMIN.password); });

type Def = { rules?: { name: string; message: string; assert: string; severity?: string; when?: string; field?: string }[]; automations?: unknown[]; security?: unknown; views?: unknown[]; reports?: unknown[]; webhooks?: unknown[]; schedules?: unknown[] };
/** Sin nulos, falsos ni listas vacías: el servidor serializa las propiedades omitidas como null. */
const strip = <T,>(v: T): T => JSON.parse(JSON.stringify(v, (_k, x) => (x === null || x === false || (Array.isArray(x) && x.length === 0) ? undefined : x)));
const def = async (name: string) => strip((await (await admin.get(`/api/model/entities/${name}`)).json()) as Def & Record<string, unknown>);
type Origin = { entity: string; installedBy: { id: string; version: string } | null; packages: { id: string }[]; elements: { kind: string; name: string; package: string | null; modified: boolean }[] };
const provenance = async () => ((await (await admin.get('/api/model/provenance')).json()) as { entities: Origin[] }).entities;

/** SQL directo contra la base desechable de la suite (solo para simular un módulo instalado en una versión anterior). */
function sql(query: string) {
  const server = process.env.E2E_SQL_SERVER ?? '(localdb)\\MSSQLLocalDB';
  const db = DB;
  if (!db.endsWith('_E2E')) throw new Error('Solo contra la base desechable.');
  execFileSync('sqlcmd', ['-S', server, '-E', '-b', '-I', '-d', db, '-Q', query], { stdio: 'pipe' });
}

/** Como `pick()` de compras: una persona solo elige lo que ve (el centro del elemento es el propio elemento). */
async function expectOnTop(el: Locator) {
  await expect(el).toBeVisible();
  expect(await el.evaluate((n) => {
    const r = n.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === n || n.contains(hit));
  }), 'la sugerencia debe verse, no quedar recortada ni tapada').toBe(true);
}

/** Spec 026 (H3 CA5): una condición vacía abre en el constructor; para escribirla a mano se pasa a «Texto». */
const textMode = async (scope: Page | Locator, label: string) =>
  scope.getByRole('group', { name: `Modo de ${label}` }).getByRole('button', { name: 'Texto' }).click();

const review = async (page: Page) => {
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  return dialog;
};
const applyFromDialog = async (page: Page) => {
  const dialog = await review(page);
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
};

test('preparar un módulo local con catálogos, un rol de servicio y un usuario sin modelado', async () => {
  admin = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(admin, { description: 'Spec 026 · catálogos de prueba', operations: [
    { op: 'setApp', app: { name: APP, label: `Pruebas del modelador ${RUN}`, icon: 'flask-conical', order: 95 } },
    { op: 'createEntity', group: APP, name: `${ZONA}`, label: 'Zona VM', labelPlural: 'Zonas VM', fields: [{ name: 'Clave', label: 'Clave', type: 'text', required: true }] },
    { op: 'createEntity', group: APP, name: `${CLIENTE}`, label: 'Cliente VM', labelPlural: 'Clientes VM', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Zona', label: 'Zona', type: 'reference', referenceEntity: `${ZONA}` },
      { name: 'UltimaNota', label: 'Última nota', type: 'text' },
    ] },
    { op: 'createEntity', group: APP, name: `${NOTA}`, label: 'Nota VM', labelPlural: 'Notas VM', fields: [
      { name: 'Titulo', label: 'Título', type: 'text', required: true },
      { name: 'Total', label: 'Total', type: 'decimal' },
      { name: 'Prioridad', label: 'Prioridad', type: 'choice', options: ['Alta', 'Baja'] },
      { name: 'Cliente', label: 'Cliente', type: 'reference', referenceEntity: `${CLIENTE}` },
      { name: 'Responsable', label: 'Responsable', type: 'user' },
    ] },
  ] });
  const role = async (name: string, data: object) => {
    const res = await admin.post('/api/admin/roles', { data: { name, description: 'E2E spec 026', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [], ...data } });
    expect(res.status(), await res.text()).toBe(200);
    return (await res.json()).id as number;
  };
  const read = { canRead: true, canCreate: false, canUpdate: false, canDelete: false };
  await role(`${SERVICIO}`, { permissions: [{ entity: `${NOTA}`, ...read }] });
  const lector = await role(`${LECTOR_ROLE}`, { permissions: [{ entity: `${NOTA}`, ...read }] });
  const res = await admin.post('/api/admin/users', { data: { username: LECTOR, displayName: LECTOR, password: PWD, roleIds: [lector] } });
  expect(res.status(), await res.text()).toBe(201);
});

// ---------------- H1: catálogos por módulo ----------------

test('H1 CA1 · CA2 · CA3 · CA4: catálogos agrupados por módulo, con filtro, búsqueda y quién los instaló', async ({ page }) => {
  await page.goto('/admin/model');
  const groups = page.locator('.catalog-group-title');
  await expect(page.getByRole('heading', { name: /^Compras · \d+ catálogos/ })).toBeVisible();
  await expect(page.getByRole('heading', { name: `Pruebas del modelador ${RUN} · 3 catálogos` })).toBeVisible();
  // CA1: en el orden de los módulos (Compras 10 antes que el de pruebas 95); General, si hay, al final.
  const titles = await groups.allInnerTexts();
  expect(titles.findIndex((t) => t.startsWith('Compras'))).toBeLessThan(titles.findIndex((t) => t.startsWith('Pruebas del modelador')));
  if (titles.some((t) => t.startsWith('General'))) expect(titles[titles.length - 1]).toMatch(/^General/);

  // CA3: quién lo instaló y quién lo extiende.
  const almacenes = page.getByRole('row', { name: /^Almacenes Almacen / });
  await expect(almacenes).toContainText(/Compras 1\.10\.0/);
  await expect(almacenes).toContainText(/También: .*Costeo/);
  await expect(page.getByRole('row', { name: new RegExp(`Notas VM ${NOTA}`) })).toContainText('Local');

  // CA2 · CA4: filtro y búsqueda en la URL; recargar los conserva.
  await page.getByLabel('Módulo', { exact: true }).selectOption(APP);
  await expect(page).toHaveURL(new RegExp(`modulo=${APP}`));
  await expect(page.getByRole('heading', { name: /^Compras/ })).toHaveCount(0);
  await page.getByLabel('Buscar catálogo').fill('nota');
  await expect(page).toHaveURL(/q=nota/);
  await expect(page.getByText('1 catálogo', { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('row', { name: new RegExp(`Notas VM ${NOTA}`) })).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(`Clientes VM ${CLIENTE}`) })).toHaveCount(0);
  await expectAccessible(page);

  // Al volver del editor se conservan.
  await page.getByRole('row', { name: new RegExp(`Notas VM ${NOTA}`) }).getByRole('button', { name: 'Editar Notas VM' }).click();
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await expect(page).toHaveURL(new RegExp(`modulo=${APP}`));
  await expect(page).toHaveURL(/q=nota/);

  await page.getByLabel('Buscar catálogo').fill('no existe nada así');
  await expect(page.getByText('Ningún catálogo coincide')).toBeVisible();
  await page.getByRole('button', { name: 'Quitar filtros' }).click();
  await expect(page.getByRole('heading', { name: /^Compras/ })).toBeVisible();
});

// ---------------- H2: origen ----------------

test('H2 CA5: el origen y la validación de expresiones son solo para quien administra el modelo', async () => {
  const lector = await apiAs(LECTOR, PWD);
  expect((await lector.get('/api/model/provenance')).status()).toBe(403);
  expect((await lector.post('/api/model/expressions/check', { data: { entity: `${NOTA}`, expression: 'Total > 0' } })).status()).toBe(403);
  expect((await lector.get('/api/model/expressions/functions')).status()).toBe(403);
  // Límites de la entrada.
  expect((await admin.post('/api/model/expressions/check', { data: { entity: 'no valido!', expression: 'Total > 0' } })).status()).toBe(400);
  expect((await admin.post('/api/model/expressions/check', { data: { entity: `${NOTA}`, expression: 'x'.repeat(2001) } })).status()).toBe(400);
  // El mismo analizador que el compilador, con los campos del borrador.
  const check = async (expression: string, fields?: object[]) =>
    ((await (await admin.post('/api/model/expressions/check', { data: { entity: `${NOTA}`, expression, fields } })).json()) as { errors: string[] }).errors;
  expect(await check('Cliente.Zona.Clave = \'N\' AND Total > 0')).toEqual([]);
  expect((await check('NoExiste > 0')).join()).toMatch(new RegExp(`'NoExiste' no existe en ${NOTA}`));
  expect(await check('Monto > 0', [{ name: 'Monto', label: 'Monto', type: 'decimal' }])).toEqual([]);
});

test('H2 CA1 · CA2: cada campo y regla dice de qué paquete viene; lo cambiado aquí, «modificado localmente»', async ({ page }) => {
  const all = await provenance();
  const almacen = all.find((e) => e.entity === 'Almacen')!;
  expect(almacen.installedBy).toMatchObject({ id: 'compras', version: '1.10.0' });
  expect(almacen.elements.find((x) => x.kind === 'field' && x.name === 'CuentaInventario')).toMatchObject({ package: 'costeo', modified: false });
  expect(almacen.elements.find((x) => x.kind === 'field' && x.name === 'Nombre')).toMatchObject({ package: 'compras', modified: false });
  expect(all.find((e) => e.entity === `${NOTA}`)).toMatchObject({ installedBy: null, packages: [] });

  await page.goto('/admin/model?edit=Almacen');
  await expect(page.getByText(/Instalado por\s*Compras 1\.10\.0/)).toBeVisible();
  await expect(page.getByText(/También lo extiende: .*Costeo/)).toBeVisible();
  const cuenta = page.locator('.field-row').filter({ has: page.locator('input[value="CuentaInventario"]') });
  await expect(cuenta.locator('.field-row-origin')).toContainText('Costeo');
  const nombre = page.locator('.field-row').filter({ has: page.locator('input[value="Nombre"]') });
  await expect(nombre.locator('.field-row-origin')).toContainText('Compras');

  // CA2: una regla de Compras cambiada en este entorno.
  const producto = await def('Producto');
  const rules = producto.rules!;
  await applyPlan(admin, { description: 'Spec 026 · cambio local', operations: [
    { op: 'setRules', entity: 'Producto', rules: rules.map((r) => (r.name === 'CostoNoNegativo' ? { ...r, message: 'Mensaje cambiado en este entorno.' } : r)) },
  ] });
  const p = (await provenance()).find((e) => e.entity === 'Producto')!;
  expect(p.elements.find((x) => x.kind === 'rule' && x.name === 'CostoNoNegativo')).toMatchObject({ package: 'compras', modified: true });
  expect(p.elements.find((x) => x.kind === 'rule' && x.name === 'IvaRango')).toMatchObject({ package: 'compras', modified: false });
  await page.goto('/admin/model?edit=Producto&tab=reglas');
  const item = page.getByRole('listitem').filter({ has: page.getByRole('button', { name: /^Mensaje cambiado en este entorno/ }) });
  await expect(item.getByText('Modificado localmente')).toBeVisible();
  await expect(item.locator('.origin-chip')).toContainText('Compras');
  // Se deja como estaba.
  await applyPlan(admin, { description: 'Spec 026 · deshacer cambio local', operations: [{ op: 'setRules', entity: 'Producto', rules }] });
});

test('H2 CA3: cambiar algo que instaló un paquete avisa junto al elemento', async ({ page }) => {
  await page.goto('/admin/model?edit=Almacen');
  const nombre = page.locator('.field-row').filter({ has: page.locator('input[value="Nombre"]') });
  await expect(nombre.getByText('Lo instaló Compras')).toHaveCount(0);
  const label = nombre.getByLabel('Etiqueta del campo');
  await label.fill('Nombre del almacén');
  await expect(nombre.getByText('Lo instaló Compras')).toBeVisible();
  await expect(nombre.getByText(/la actualización lo reemplaza/)).toBeVisible();
  await expectAccessible(page);
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await page.getByRole('button', { name: 'Descartar cambios' }).click();
  // En una regla: el aviso aparece al cambiarla en el borrador.
  await page.goto('/admin/model?edit=Producto&tab=reglas');
  // Por su identificador: el título del elemento es su mensaje, que se va a cambiar.
  const regla = page.getByRole('listitem').filter({ hasText: 'CostoNoNegativo' });
  await regla.getByRole('button', { name: /^El costo no puede ser negativo/ }).click();
  await regla.getByLabel('Mensaje').fill('El costo nunca es negativo.');
  await expect(regla.getByText('Lo instaló Compras')).toBeVisible();
  await page.getByRole('button', { name: 'Cancelar' }).click();
  await page.getByRole('button', { name: 'Descartar cambios' }).click();
});

test('H2 CA3: abrir un elemento instalado sin cambiarlo no avisa de cambios locales', async ({ page }) => {
  // Regresión (revisión exploratoria 026, F-03): lo guardado trae nulos que el borrador no; al desplegar «Póliza de
  // entrada» (Costeo) salía «Lo instaló Costeo · tu cambio queda como modificación local» sin haber cambiado nada.
  await page.goto('/admin/model?edit=Recepcion&tab=automatizaciones');
  const item = page.getByRole('listitem').filter({ hasText: 'PolizaEntrada' }).first();
  await item.getByRole('button', { name: /^Póliza de entrada/ }).click();
  await expect(item.getByLabel('Tipo de acción').first()).toHaveValue('create');
  await expect(item.locator('.origin-chip').first()).toContainText('Costeo'); // el origen ya llegó
  await expect(page.getByText(/^Lo instaló/)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
});

test('H2 CA4: la vista previa de actualizar un módulo lista lo local y lo modificado que la actualización pisa', async ({ page }) => {
  const producto = await def('Producto');
  const rules = producto.rules!;
  await applyPlan(admin, { description: 'Spec 026 · cambios locales antes de actualizar', operations: [
    { op: 'setRules', entity: 'Producto', rules: [...rules.map((r) => (r.name === 'CostoNoNegativo' ? { ...r, message: 'Cambiado aquí.' } : r)),
      { name: 'ReglaLocal026', assert: 'TRUE', message: 'Regla creada en este entorno.' }] },
  ] });
  // Como si Compras estuviera en la 1.7.0: la 1.8.0 reemplaza las reglas de Producto sin merge. Al terminar se deja la
  // versión que estaba (spec 027: no se fija aquí, cada spec puede subirla).
  const installed = (await (await admin.get('/api/admin/modules')).json()).find((m: { id: string }) => m.id === 'compras').installedVersion as string;
  sql("UPDATE meta.Module SET Version = '1.7.0' WHERE Id = 'compras'");
  try {
    const preview = await (await admin.post('/api/admin/modules/compras/upgrade/preview')).json();
    expect(preview.conflicts).toEqual(expect.arrayContaining([
      expect.objectContaining({ entity: 'Producto', kind: 'rule', name: 'ReglaLocal026', reason: 'local', effect: 'removed' }),
      expect.objectContaining({ entity: 'Producto', kind: 'rule', name: 'CostoNoNegativo', reason: 'modified', effect: 'replaced' }),
    ]));
    expect(preview.conflicts.some((c: { name: string }) => c.name === 'IvaRango')).toBe(false); // sin tocar aquí: no es un choque

    await page.goto('/admin/modules');
    await page.getByRole('button', { name: 'Actualizar Compras' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText(/La actualización reemplaza \d+ cambios? locales?/)).toBeVisible();
    await expect(dialog.getByText(/ReglaLocal026.*creado aquí; la actualización lo quita/)).toBeVisible();
    await expect(dialog.getByText(/CostoNoNegativo.*modificado aquí; la actualización lo reemplaza/)).toBeVisible();
    await expectAccessible(page);
    await page.keyboard.press('Escape');
  } finally {
    sql(`UPDATE meta.Module SET Version = '${installed}' WHERE Id = 'compras'`);
    await applyPlan(admin, { description: 'Spec 026 · deshacer cambios locales', operations: [{ op: 'setRules', entity: 'Producto', rules }] });
  }
  // Sin cambios locales no hay choques (lo dice la unitaria con la cadena real); aquí, ya no hay actualización.
  expect((await admin.post('/api/admin/modules/compras/upgrade/preview')).status()).toBe(409);
});

// ---------------- H3: editor de expresiones ----------------

test('H3 CA1 · CA2 · CA3: autocompletado de campos, rutas, funciones y opciones, con teclado y validación en vivo', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}`);
  // Un campo nuevo del borrador ya se puede usar en una expresión (se manda al validar).
  await page.getByRole('button', { name: 'Agregar campo' }).click();
  await page.getByLabel('Etiqueta del campo').last().fill('Monto');
  await page.getByRole('tab', { name: /^Reglas/ }).click();
  await page.getByRole('button', { name: 'Agregar regla' }).click();
  await expect(page.getByLabel('Identificador')).toBeFocused();
  await textMode(page, 'Condición que debe cumplirse');
  const cond =page.getByRole('combobox', { name: /Condición que debe cumplirse/ });

  // Campos: escribir «Tot» sugiere Total; Enter lo inserta.
  await cond.click();
  await cond.pressSequentially('Tot');
  const list = page.getByRole('listbox', { name: 'Sugerencias' });
  await expectOnTop(list.getByRole('option', { name: /Total/ }));
  await expect(cond).toHaveAttribute('aria-expanded', 'true');
  await cond.press('Enter');
  await expect(cond).toHaveValue('Total');
  await expect(list).toBeHidden();

  // Rutas de tres niveles por referencias.
  await cond.fill('');
  await cond.pressSequentially('Cliente.Zo');
  await expect(list.getByRole('option', { name: /Zona/ })).toBeVisible();
  await cond.press('Tab');
  await expect(cond).toHaveValue('Cliente.Zona');
  await cond.pressSequentially('.');
  await expectOnTop(list.getByRole('option', { name: /Clave/ }));
  await cond.press('ArrowDown'); // con una sola opción, la flecha se queda en ella
  await cond.press('Enter');
  await expect(cond).toHaveValue('Cliente.Zona.Clave');

  // Opciones de una lista después de «Campo = '».
  await cond.fill('');
  await cond.pressSequentially("Prioridad = 'A");
  await expect(list.getByRole('option', { name: 'Alta' })).toBeVisible();
  await cond.press('Enter');
  await expect(cond).toHaveValue("Prioridad = 'Alta'");

  // Funciones con su firma; Esc cierra la lista sin tocar el texto.
  await cond.pressSequentially(' AND ISB');
  await expect(list.getByRole('option', { name: /ISBLANK/ })).toContainText('ISBLANK(valor)');
  await cond.press('Escape');
  await expect(list).toBeHidden();
  await expect(cond).toHaveValue("Prioridad = 'Alta' AND ISB");

  // CA3: validación en vivo con el analizador del servidor, también con el campo nuevo del borrador.
  await cond.fill('NoExiste > 0');
  await expect(page.locator('.expr-msg').getByText(`El campo 'NoExiste' no existe en ${NOTA}.`)).toBeVisible();
  await expect(cond).toHaveAttribute('aria-invalid', 'true');
  await cond.fill('Monto > 0 AND Cliente.Zona.Clave <> \'X\'');
  await expect(page.locator('.expr-msg').getByText('Expresión válida').first()).toBeVisible();
  await expect(cond).not.toHaveAttribute('aria-invalid', 'true');
  await expectAccessible(page);
});

test('H3 CA5: constructor de condiciones — listas, Y/O, rutas, «es uno de», usuario y vuelta a texto', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}&tab=reglas`);
  await page.getByRole('button', { name: 'Agregar regla' }).click();
  const g = page.getByRole('group', { name: /^Condición que debe cumplirse/ });
  const asText = async () => {
    await textMode(page, 'Condición que debe cumplirse');
    const v = await g.getByRole('combobox').inputValue();
    await g.getByRole('button', { name: 'Constructor' }).click();
    return v;
  };
  // Vacía: abre en el constructor.
  await expect(g.getByRole('button', { name: 'Constructor' })).toHaveAttribute('aria-pressed', 'true');

  // Renglón 1: «es uno de» con las opciones del campo.
  await g.getByRole('button', { name: 'Agregar condición' }).click();
  await g.getByLabel('Condición 1: campo', { exact: true }).selectOption('Prioridad');
  await expect(g.getByLabel('Condición 1: operador').locator('option')).toHaveText(['es igual a', 'es distinto de', 'es uno de', 'está vacío', 'no está vacío']);
  await g.getByLabel('Condición 1: operador').selectOption('in');
  const row1 = g.getByRole('group', { name: /condición 1$/ });
  await row1.getByLabel('Alta').check();
  await row1.getByLabel('Baja').check();
  // Renglón 2: una ruta de dos saltos por referencias.
  await g.getByRole('button', { name: 'Agregar condición' }).click();
  await g.getByLabel('Condición 2: campo', { exact: true }).selectOption('Cliente');
  await g.getByLabel('Condición 2: campo de Cliente', { exact: true }).selectOption('Zona');
  await g.getByLabel('Condición 2: campo de Cliente.Zona', { exact: true }).selectOption('Clave');
  await g.getByLabel('Condición 2: valor').fill("O'Higgins");
  await g.getByLabel(/cómo se combinan/).selectOption('OR');
  await expect(g.locator('.expr-msg').getByText('Expresión válida')).toBeVisible();
  expect(await asText()).toBe("Prioridad IN ('Alta', 'Baja') OR Cliente.Zona.Clave = 'O''Higgins'");

  // Número, usuario («quien usa el sistema») y vacío.
  await g.getByRole('button', { name: 'Quitar condición 2' }).click();
  await g.getByRole('button', { name: 'Quitar condición 1' }).click();
  await g.getByRole('button', { name: 'Agregar condición' }).click();
  await g.getByLabel('Condición 1: campo', { exact: true }).selectOption('Total');
  await g.getByLabel('Condición 1: operador').selectOption('gte');
  await g.getByLabel('Condición 1: valor').fill('10.5');
  await g.getByRole('button', { name: 'Agregar condición' }).click();
  await g.getByLabel('Condición 2: campo', { exact: true }).selectOption('Responsable');
  await g.getByRole('button', { name: 'Agregar condición' }).click();
  await g.getByLabel('Condición 3: campo', { exact: true }).selectOption('Titulo');
  await g.getByLabel('Condición 3: operador').selectOption('notBlank');
  await g.getByLabel(/cómo se combinan/).selectOption('AND'); // seguía en «alguna (O)» del paso anterior
  expect(await asText()).toBe('Total >= 10.5 AND Responsable = CURRENTUSER() AND NOT ISBLANK(Titulo)');

  // Lo escrito a mano con forma simple vuelve a renglones; lo complejo se queda en texto y lo dice.
  await textMode(page, 'Condición que debe cumplirse');
  const box = g.getByRole('combobox');
  await box.fill("Prioridad <> 'Baja' AND ISBLANK(Cliente)");
  await g.getByRole('button', { name: 'Constructor' }).click();
  await expect(g.getByLabel('Condición 1: valor')).toHaveValue('Baja');
  await expect(g.getByLabel('Condición 2: operador')).toHaveValue('blank');
  await textMode(page, 'Condición que debe cumplirse');
  await box.fill('ROUND(Total, 0) > 2 OR (Total > 1 AND Total < 5)');
  await expect(g.getByRole('button', { name: 'Constructor' })).toBeDisabled();
  await expect(g.getByText(/se edita como texto/)).toBeVisible();
  await expectAccessible(page);
});

test('H3 CA4: la fórmula y el valor por defecto de un campo usan el editor de expresiones', async ({ page }) => {
  // Regresión (CI de la 033, R-030-5): el editor carga la lista de funciones al montarse; si «ROU» se escribía antes de que
  // llegara, no aparecía la sugerencia (solo en el runner lento de CI). Se espera la lista antes de escribir.
  const functions = page.waitForResponse((r) => r.url().includes('/api/model/expressions/functions'));
  await page.goto(`/admin/model?edit=${NOTA}`);
  await page.getByRole('button', { name: 'Mostrar opciones de Total' }).click();
  expect((await functions).ok()).toBe(true);
  const byDefault = page.getByRole('combobox', { name: /Valor por defecto/ });
  await byDefault.pressSequentially('ROU');
  await expect(page.getByRole('option', { name: /ROUND/ })).toBeVisible();
  await byDefault.press('Enter');
  await expect(byDefault).toHaveValue('ROUND(');
  await byDefault.fill('Totl * 2');
  await expect(page.locator('.expr-msg').getByText(`El campo 'Totl' no existe en ${NOTA}.`)).toBeVisible();
});

// ---------------- H4: editores visuales ----------------

test('H4 CA9: cada sección vacía tiene su estado vacío y ningún texto de ejemplo parece del catálogo', async ({ page }) => {
  // Regresión (hallazgo 3 del dueño): los placeholder de las áreas JSON mostraban reglas de ejemplo (FechaValida, AlConfirmar).
  const empty: [string, RegExp][] = [
    ['reglas', /Este catálogo no tiene reglas/], ['automatizaciones', /Este catálogo no tiene automatizaciones/],
    ['seguridad', /Este catálogo no tiene seguridad propia/], ['vistas', /Este catálogo no tiene vistas compartidas/],
    ['integraciones', /Este catálogo no tiene webhooks/], ['contabilidad', /Este catálogo no es un libro de partida doble/],
  ];
  for (const [tab, text] of empty) {
    await page.goto(`/admin/model?edit=${NOTA}&tab=${tab}`);
    await expect(page.getByText(text)).toBeVisible();
    for (const s of await page.locator('details.json-advanced summary').all()) await s.click();
    await expect(page.locator('textarea[placeholder*="FechaValida"], textarea[placeholder*="AlConfirmar"], textarea[placeholder*="Mias"], textarea[placeholder*="PorMes"]')).toHaveCount(0);
    await expect(page.getByText(/FechaValida|AlConfirmar/)).toHaveCount(0);
  }
  await page.goto(`/admin/model?edit=${NOTA}&tab=vistas`);
  await expect(page.getByText('Este catálogo no tiene reportes')).toBeVisible();
  await page.goto(`/admin/model?edit=${NOTA}&tab=integraciones`);
  await expect(page.getByText('Este catálogo no tiene tareas programadas')).toBeVisible();
});

test('H4 CA1 · CA10: reglas con el editor visual; el JSON avanzado escribe el mismo borrador', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}&tab=reglas`);
  // El JSON está plegado por defecto.
  await expect(page.getByLabel('Reglas (JSON)')).toBeHidden();
  await page.getByRole('button', { name: 'Agregar regla' }).click();
  await page.getByLabel('Identificador').fill('TotalPositivo');
  await page.getByLabel('Mensaje').fill('El total debe ser positivo.');
  // La condición con el constructor, sin escribir: Total · es mayor que · 0.
  const assert = page.getByRole('group', { name: /^Condición que debe cumplirse/ });
  await assert.getByRole('button', { name: 'Agregar condición' }).click();
  await assert.getByLabel('Condición 1: campo').selectOption('Total');
  await assert.getByLabel('Condición 1: operador').selectOption('gt');
  await expect(assert.getByLabel('Condición 1: valor')).toHaveValue('0');
  await page.getByLabel('Severidad').selectOption('warning');
  await page.getByLabel('Campo donde se muestra').selectOption('Total');
  const box = await openJson(page, 'Reglas (JSON)');
  await expect(box).toHaveValue(/"name": "TotalPositivo"/);
  await expect(box).toHaveValue(/"severity": "warning"/);

  // Lo escrito en el JSON se ve en el editor visual.
  const json = JSON.parse(await box.inputValue());
  json[0].message = 'Mensaje escrito como JSON.';
  await box.fill(JSON.stringify(json, null, 2));
  await expect(page.getByRole('button', { name: /^Mensaje escrito como JSON/ })).toBeVisible();
  // Un JSON inválido no se pierde: el editor visual lo dice y el texto queda.
  await box.fill('[ { "name": ');
  await expect(page.getByText('El JSON de esta sección no es válido')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Agregar regla' })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Reglas' })).toBeVisible();
  await box.fill(JSON.stringify(json, null, 2));
  await expect(page.getByRole('button', { name: /^Mensaje escrito como JSON/ })).toBeVisible();

  const dialog = await review(page);
  await expect(dialog.getByText(/Definir 1 regla\(s\) de negocio en 'Nota VM'/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  expect((await def(`${NOTA}`)).rules).toEqual([{ name: 'TotalPositivo', message: 'Mensaje escrito como JSON.', assert: 'Total > 0', severity: 'warning', field: 'Total' }]);
});

test('H4 CA2 · CA11: automatización «cuando → si → hacer» con listas; los errores del compilador se ven en la sección', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}&tab=automatizaciones`);
  await page.getByRole('button', { name: 'Agregar automatización' }).click();
  await page.getByLabel('Nombre visible').fill('Avisar al cliente');
  await page.getByLabel('Identificador').fill('AvisarCliente');
  await page.getByLabel('Evento').selectOption('create');
  const si = page.getByRole('group', { name: /^Si \(condición/ });
  await si.getByRole('button', { name: 'Agregar condición' }).click();
  await si.getByLabel('Condición 1: campo').selectOption('Cliente');
  await si.getByLabel('Condición 1: operador').selectOption('notBlank');
  await page.getByRole('button', { name: 'Agregar acción' }).click();
  await page.getByLabel('Tipo de acción').selectOption('update');
  await page.getByLabel('Registro que se modifica').selectOption('Cliente');
  await page.getByRole('button', { name: 'Agregar campo' }).click();
  // CA11: sin campo elegido, el compilador lo rechaza y se ve junto a la sección y al elemento, con la pestaña marcada.
  await expect(page.getByText('El compilador rechaza esta sección')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByRole('listitem').filter({ hasText: 'AvisarCliente' }).getByText('Errores del compilador')).toBeVisible();
  await expect(page.getByText(`no se puede asignar '' en ${CLIENTE}`).first()).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Automatizaciones (con errores)' })).toBeVisible();
  await page.getByLabel('Campos que se asignan: campo 1').selectOption('UltimaNota');
  await page.getByRole('combobox', { name: /Campos que se asignan: valor de UltimaNota/ }).fill('Titulo');
  await expect(page.getByText('El compilador rechaza esta sección')).toBeHidden({ timeout: 10_000 });
  await expect(page.getByText('El compilador no encontró errores.')).toBeVisible();
  await expectAccessible(page);
  await applyFromDialog(page);

  expect((await def(`${NOTA}`)).automations).toEqual([{ name: 'AvisarCliente', label: 'Avisar al cliente', on: 'create', when: 'NOT ISBLANK(Cliente)',
    actions: [{ type: 'update', target: 'Cliente', set: [{ field: 'UltimaNota', value: 'Titulo' }] }] }]);
  // Y funciona: crear una nota actualiza su cliente.
  const cliente = await (await admin.post(`/api/data/${CLIENTE}`, { data: { Nombre: 'Cliente 026' } })).json();
  expect((await admin.post(`/api/data/${NOTA}`, { data: { Titulo: 'Hola desde la 026', Cliente: cliente.id } })).status()).toBe(201);
  expect((await (await admin.get(`/api/data/${CLIENTE}/${cliente.id}`)).json()).UltimaNota).toBe('Hola desde la 026');
});

test('H4 CA3: seguridad por registro y por campo con listas de roles y alcance', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}&tab=seguridad`);
  await page.getByRole('button', { name: 'Definir seguridad' }).click();
  await page.getByLabel('Campo dueño').selectOption('Responsable');
  await page.getByRole('button', { name: 'Agregar regla por registro' }).click();
  await page.getByLabel('Regla 1: alcance').selectOption('own');
  await page.getByRole('group', { name: 'Regla 1: roles' }).getByLabel(`${LECTOR_ROLE}`).check();
  await page.getByLabel('Regla 1: acceso').selectOption('read');
  await page.getByRole('button', { name: 'Restringir un campo' }).click();
  await page.getByLabel('Campo 1', { exact: true }).selectOption('Total');
  await page.getByRole('group', { name: 'Campo 1: roles que lo ven' }).getByLabel(`${SERVICIO}`).check();
  await expectAccessible(page);
  await applyFromDialog(page);
  expect((await def(`${NOTA}`)).security).toEqual({
    // effectiveScope es solo de salida (lo calcula el servidor).
    owner: 'Responsable', records: [expect.objectContaining({ scope: 'own', roles: [`${LECTOR_ROLE}`], access: 'read' })], fields: [{ field: 'Total', read: [`${SERVICIO}`] }],
  });
});

test('H4 CA4 · CA5 · CA12: vista y reporte con selectores; el editor conserva lo que no muestra', async ({ page }) => {
  await applyPlan(admin, { description: 'Spec 026 · vista con etiqueta de filtro', operations: [
    { op: 'setViews', entity: `${NOTA}`, views: [{ name: 'Altas', label: 'Altas', filters: [{ field: 'Prioridad', op: 'eq', value: 'Alta', label: 'Prioridad alta' }] }] },
  ] });
  await page.goto(`/admin/model?edit=${NOTA}&tab=vistas`);
  await page.getByRole('button', { name: /^Altas/ }).click();
  await page.getByLabel('Nombre visible').fill('Notas altas');
  await page.getByLabel('Columnas: agregar').selectOption('Titulo');
  await page.getByLabel('Columnas: agregar').selectOption('Total');
  await page.getByRole('button', { name: 'Subir Total' }).click();
  await page.getByLabel('Ordenar por').selectOption('Total');
  await page.getByLabel('Dirección').first().selectOption('desc');
  await page.getByRole('switch', { name: 'Vista predeterminada' }).check({ force: true });

  await page.getByRole('button', { name: 'Agregar reporte' }).click();
  const rep = page.getByRole('region', { name: 'Reportes' });
  await rep.getByLabel('Nombre visible').fill('Por prioridad');
  await rep.getByLabel('Identificador').fill('PorPrioridad');
  await rep.getByRole('button', { name: 'Agregar agrupación' }).click();
  await rep.getByLabel('Agrupación 1: campo').selectOption('Prioridad');
  await rep.getByRole('button', { name: 'Agregar medida' }).click();
  await rep.getByLabel('Medida 2: campo').selectOption('Total');
  await rep.getByLabel('Tipo de gráfica').selectOption('bar'); // spec 039: «Gráfica» es ahora la sección
  await applyFromDialog(page);

  const d = await def(`${NOTA}`);
  expect(d.views).toEqual([{ name: 'Altas', label: 'Notas altas', columns: ['Total', 'Titulo'], sort: 'Total', dir: 'desc', default: true,
    filters: [{ field: 'Prioridad', op: 'eq', value: 'Alta', label: 'Prioridad alta' }] }]); // CA12: el label del filtro sigue
  expect(d.reports).toEqual([{ name: 'PorPrioridad', label: 'Por prioridad', measures: [{ fn: 'count' }, { fn: 'sum', field: 'Total' }], groupBy: [{ field: 'Prioridad' }], chart: 'bar' }]);
});

test('H4 CA6 · CA7: webhook y tarea programada con eventos, rol de servicio y calendario', async ({ page }) => {
  await page.goto(`/admin/model?edit=${NOTA}&tab=integraciones`);
  await page.getByRole('button', { name: 'Agregar webhook' }).click();
  const hooks = page.getByRole('region', { name: 'Webhooks' });
  await hooks.getByLabel('Nombre visible').fill('Nota nueva');
  await hooks.getByLabel('Identificador').fill('NotaNueva');
  await hooks.getByLabel('Rol de servicio').selectOption(`${SERVICIO}`);
  await hooks.getByRole('group', { name: 'Campos que se envían' }).getByLabel('Título').check();

  await page.getByRole('button', { name: 'Agregar tarea' }).click();
  const tasks = page.getByRole('region', { name: 'Tareas programadas' });
  await tasks.getByLabel('Nombre visible').fill('Marcar altas');
  await tasks.getByLabel('Identificador').fill('MarcarAltas');
  await tasks.getByLabel('Frecuencia').selectOption('week');
  await tasks.getByRole('group', { name: 'Días' }).getByLabel('Lunes').check(); // ya viene marcado el lunes
  await tasks.getByRole('group', { name: 'Días' }).getByLabel('Viernes').check();
  await tasks.getByLabel('Hora').fill('08:30');
  const where = tasks.getByRole('group', { name: /^Qué registros/ });
  await where.getByRole('button', { name: 'Agregar condición' }).click();
  await where.getByLabel('Condición 1: campo').selectOption('Prioridad');
  await expect(where.getByLabel('Condición 1: valor')).toHaveValue('Alta');
  await tasks.getByLabel('Rol de servicio').selectOption(`${SERVICIO}`);
  await tasks.getByRole('button', { name: 'Agregar acción' }).click();
  await tasks.getByLabel('Tipo de acción').selectOption('update');
  await tasks.getByRole('button', { name: 'Agregar campo' }).click();
  await tasks.getByLabel('Campos que se asignan: campo 1').selectOption('Total');
  await tasks.getByRole('combobox', { name: /valor de Total/ }).fill('0');
  await applyFromDialog(page);

  const d = await def(`${NOTA}`);
  expect(d.webhooks).toEqual([{ name: 'NotaNueva', label: 'Nota nueva', events: [{ on: 'create' }], role: `${SERVICIO}`, fields: ['Titulo'] }]);
  expect(d.schedules).toEqual([expect.objectContaining({ name: 'MarcarAltas', every: 'week', weekdays: [1, 5], at: '08:30', where: "Prioridad = 'Alta'", role: `${SERVICIO}`,
    actions: [{ type: 'update', set: [{ field: 'Total', value: '0' }] }] })]);
});

test('H4 CA8: el libro de partida doble se edita con listas y dice de qué paquete viene', async ({ page }) => {
  const poliza = (await (await admin.get('/api/model/entities?includeChildren=true')).json() as { name: string; ledger?: unknown }[]).find((e) => e.ledger)!;
  await page.goto(`/admin/model?edit=${poliza.name}&tab=contabilidad`);
  await expect(page.getByLabel('Partidas')).not.toHaveValue('');
  await expect(page.getByLabel('Cargo (campo de la partida)')).not.toHaveValue('');
  await expect(page.getByLabel('Abono (campo de la partida)')).not.toHaveValue('');
  await expect(page.getByLabel('Catálogo de periodos')).not.toHaveValue('');
  await expect(page.getByRole('region', { name: 'Libro de partida doble' }).getByText(/Contabilidad/).first()).toBeVisible();
  // La condición del periodo (Estado = 'Abierto') se lee en el constructor.
  const open = page.getByRole('group', { name: /^Condición de periodo abierto/ });
  await expect(open.getByLabel('Condición 1: campo', { exact: true })).not.toHaveValue('');
  await expect(open.getByLabel('Condición 1: valor')).not.toHaveValue('');
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
  await expectAccessible(page);
});

test('H4 CA13: con teclado, en oscuro y en móvil (390 px) sin desbordamiento', async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json', colorScheme: 'dark', viewport: { width: 390, height: 844 } });
  const page = await ctx.newPage();
  await page.addInitScript(() => { try { localStorage.setItem('erp.theme', 'dark'); } catch { /* sin storage */ } });
  await page.goto(`/admin/model?edit=${NOTA}&tab=automatizaciones`);
  // Abrir la automatización con teclado.
  const toggle = page.getByRole('button', { name: /^Avisar al cliente/ });
  await toggle.focus();
  await page.keyboard.press('Enter');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  // Las pestañas se recorren con flechas y el foco las sigue. Regresión (revisión exploratoria 026, F-01).
  const tab = page.getByRole('tab', { name: 'Automatizaciones' });
  await tab.focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Seguridad' })).toBeFocused();
  await expect(page.getByRole('tabpanel', { name: 'Seguridad' })).toBeVisible(); // la otra pestaña ya se montó
  await page.keyboard.press('ArrowLeft');
  await expect(page.getByRole('tab', { name: 'Automatizaciones' })).toBeFocused();
  // Al volver, la pestaña se montó de nuevo: la automatización está plegada.
  const again = page.getByRole('button', { name: /^Avisar al cliente/ });
  await expect(again).toHaveAttribute('aria-expanded', 'false');
  await again.click();
  await expect(page.getByLabel('Registro que se modifica')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await expectAccessible(page);
  await page.goto('/admin/model');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await ctx.close();
});

test('H4 CA13: capturas de referencia del editor de automatizaciones y de la lista por módulos', async ({ page }) => {
  // Ventana alta: la región completa cabe y se pinta entera (una captura de elemento fuera de la ventana sale en blanco).
  await page.setViewportSize({ width: 1280, height: 1900 });
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    await page.goto(`/admin/model?edit=${NOTA}&tab=automatizaciones`);
    await page.getByRole('button', { name: /^Avisar al cliente/ }).click();
    await expect(page.getByLabel('Registro que se modifica')).toHaveValue('Cliente');
    // La validación en vivo termina antes de la captura (si no, mostraría «Validando…» o la ayuda según el momento).
    await expect(page.locator('.expr-msg').getByText('Expresión válida')).toHaveCount(2);
    await expect(page.getByRole('region', { name: 'Automatizaciones' })).toHaveScreenshot(`automatizaciones-${scheme}.png`);
    await page.goto(`/admin/model?modulo=${APP}`);
    await expect(page.getByRole('row', { name: new RegExp(`Notas VM ${NOTA}`) })).toBeVisible();
    await expect(page.getByRole('main')).toHaveScreenshot(`catalogos-por-modulo-${scheme}.png`, { mask: [page.locator('.catalog-group-title'), page.locator('td.mono'), page.getByLabel('Módulo', { exact: true })] });
  }
});
