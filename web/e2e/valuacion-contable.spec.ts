// Spec 020 · Contabilidad y costeo. Corre después de inventario.spec.ts (Compras e Inventario instalados, usuarios de
// compras.spec.ts) y antes de visual.spec.ts. Instala Contabilidad y Costeo; los datos de apoyo se preparan por API y lo que
// se prueba (errores del libro, pólizas, valuación) se revisa por la UI cuando una persona lo vería.
import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { ADMIN, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'frase larga de compras 2026'; // usuarios de compras.spec.ts e inventario.spec.ts
const CONTADOR = 'e2e-contador';
const users = { comprador: 'e2e-comprador', gerente: 'e2e-gerente', almacen: 'e2e-almacen', supervisor: 'e2e-supervisor', contador: CONTADOR } as const;
type Who = keyof typeof users | 'admin';
type Row = { id: number; _rowVersion: string; [k: string]: unknown };

const api: Partial<Record<Who, APIRequestContext>> = {};
const as = async (w: Who) => (api[w] ??= w === 'admin' ? await apiAs(ADMIN.username, ADMIN.password) : await apiAs(users[w], PWD));

async function call(res: { status(): number; json(): Promise<unknown> }) {
  return { status: res.status(), body: (await res.json().catch(() => null)) as Row & { title?: string; errors?: Record<string, string[]>; items: Row[] } };
}
const post = async (w: Who, url: string, data: object) => call(await (await as(w)).post(url, { data }));
const put = async (w: Who, url: string, data: object) => call(await (await as(w)).put(url, { data }));
const get = async (w: Who, url: string, params?: Record<string, string | number>) => (await (await as(w)).get(url, { params })).json();
const transition = async (w: Who, entity: string, row: Row, name: string, extra: object = {}) =>
  post(w, `/api/data/${entity}/${row.id}/transition`, { transition: name, _rowVersion: row._rowVersion, ...extra });

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const now = new Date();
const TODAY = iso(now);
const MONTH_START = iso(new Date(now.getFullYear(), now.getMonth(), 1));
const MONTH_END = iso(new Date(now.getFullYear(), now.getMonth() + 1, 0));
const NEXT_YEAR = `${now.getFullYear() + 1}-06-15`;

const ids = { almacen1: 0, almacen2: 0, proveedor: 0, rodamiento: 0, promedio: 0, fifo: 0, periodo: 0, eur: 0 };
const cuentas: Record<string, number> = {};

async function pageAs(browser: Browser, w: keyof typeof users): Promise<Page> {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, users[w], PWD);
  return page;
}

/** Ejecuta una acción del proceso desde la UI: botón y confirmación del diálogo. */
async function act(page: Page, name: string) {
  await page.getByRole('button', { name }).click();
  await page.getByRole('dialog').getByRole('button', { name }).click();
}

async function existencia(almacen: number, producto: number) {
  const r = await get('supervisor', '/api/data/Existencia', { pageSize: 200 });
  return r.items.find((x: Row) => x.Almacen === almacen && x.Producto === producto) as Row | undefined;
}
async function movimiento(w: Who, data: object) {
  const r = await post(w, '/api/data/MovimientoInventario', data);
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
}
async function cuenta(codigo: string) {
  return (await get('contador', `/api/data/CuentaContable/${cuentas[codigo]}`)) as Row;
}
async function poliza(partidas: { cuenta: string; cargo?: number; abono?: number }[], extra: object = {}) {
  const r = await post('contador', '/api/data/Poliza', {
    Concepto: 'Póliza de prueba', ...extra,
    Partidas: partidas.map((p) => ({ Cuenta: cuentas[p.cuenta], Cargo: p.cargo ?? 0, Abono: p.abono ?? 0 })),
  });
  expect(r.status, JSON.stringify(r.body)).toBe(201);
  return r.body;
}
/** Orden de compra enviada al proveedor (el flujo de autorización de Compras). */
async function ordenEnviada(data: object): Promise<Row> {
  const created = await post('comprador', '/api/data/OrdenCompra', data);
  expect(created.status, JSON.stringify(created.body)).toBe(201);
  const sent = await transition('comprador', 'OrdenCompra', created.body, 'enviar');
  const auth = await transition('gerente', 'OrdenCompra', sent.body, 'autorizar');
  const oc = await transition('comprador', 'OrdenCompra', auth.body, 'enviarProveedor');
  expect(oc.status, JSON.stringify(oc.body)).toBe(200);
  return oc.body;
}

test.afterAll(async () => {
  // visual.spec.ts espera una sola empresa activa (la de esta prueba se desactiva).
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  for (const c of (await (await admin.get('/api/admin/companies')).json()) as { id: number; code: string; name: string; baseCurrency: string; isActive: boolean }[])
    if (c.code === 'E2E-COSTOS' && c.isActive)
      await admin.put(`/api/admin/companies/${c.id}`, { data: { name: c.name, baseCurrency: c.baseCurrency, isActive: false } });
  await admin.dispose();
});

test('H4 CA1 · H5 CA4: Costeo pide Contabilidad; ambos se instalan desde la UI', async ({ page }) => {
  await page.goto('/admin/modules');
  const costeo = page.locator('article, .card').filter({ has: page.getByRole('heading', { name: 'Costeo' }) }).first();
  await expect(costeo.getByText('Requiere Contabilidad')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Instalar Costeo' })).toBeDisabled();

  await page.getByRole('button', { name: 'Instalar Contabilidad' }).click();
  let dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText(/Declarar 'Póliza' como libro de partida doble/)).toBeVisible();
  await expect(dialog.getByText(/Contador/).first()).toBeVisible(); // roles nuevos del módulo
  await expect(dialog.getByText(/Cierre contable/).first()).toBeVisible();
  await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
  await expect(page.getByText('Módulo instalado')).toBeVisible({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Instalar Costeo' }).click();
  dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  // Spec 022: Costeo 1.1.0 vuelve a declarar «Valuar» con sus políticas, así que aparece en la base y en v1.1.0.
  for (const step of ['base', 'v1.1.0'])
    await expect(dialog.getByText(new RegExp(`^\\[${step}\\] Agregar o actualizar 1 automatización\\(es\\) en 'Movimiento de inventario': Valuar el movimiento`))).toBeVisible();
  await dialog.getByRole('button', { name: 'Instalar módulo' }).click();
  await expect(page.getByText('Módulo instalado')).toBeVisible({ timeout: 30_000 });

  // Regresión (revisión exploratoria 020, F-03): «Contabilidad» no cabía junto a «Instalado» y pasaba por debajo de la insignia.
  // Se recorren anchos: con 3 o 4 tarjetas por renglón (menú lateral abierto o contraído) el espacio del nombre cambia.
  const overflow: string[] = [];
  for (let width = 900; width <= 1700; width += 40) {
    await page.setViewportSize({ width, height: 800 });
    const cut = await page.getByRole('article').getByRole('heading', { level: 2 }).evaluateAll((hs) =>
      hs.filter((h) => h.scrollWidth > h.clientWidth).map((h) => h.textContent ?? ''));
    overflow.push(...cut.map((n) => `${n} @${width}px`));
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  expect(overflow).toEqual([]);

  const nav = page.getByLabel('Navegación principal');
  for (const link of ['Pólizas', 'Cuentas contables', 'Periodos contables']) await expect(nav.getByRole('link', { name: link })).toBeVisible();
  // Catálogo mínimo en la empresa de quien instala.
  const seeded = await get('admin', '/api/data/CuentaContable', { pageSize: 50 });
  expect(seeded.items.map((c: Row) => c.Codigo)).toEqual(expect.arrayContaining(['1100', '1200', '2150', '3100', '5100']));
  expect(seeded.items.find((c: Row) => c.Codigo === '2150').Naturaleza).toBe('Acreedora');
});

test('preparar contador, periodo del mes, productos y cuentas del almacén', async () => {
  const admin = await as('admin');
  const res = await admin.post('/api/admin/users', { data: { username: CONTADOR, displayName: 'Contador E2E', password: PWD, roleIds: [await roleId(admin, 'Contador')] } });
  expect(res.status(), await res.text()).toBe(201);
  for (const c of (await get('contador', '/api/data/CuentaContable', { pageSize: 50 })).items as Row[]) cuentas[c.Codigo as string] = c.id;

  const periodo = await post('contador', '/api/data/PeriodoContable', { Nombre: 'Mes en curso', Inicio: MONTH_START, Fin: MONTH_END });
  expect(periodo.status, JSON.stringify(periodo.body)).toBe(201);
  ids.periodo = periodo.body.id;

  const almacenes = (await get('admin', '/api/data/Almacen', { pageSize: 50 })).items as Row[];
  ids.almacen1 = almacenes.find((a) => a.Clave === 'ALM-01')!.id;
  ids.almacen2 = almacenes.find((a) => a.Clave === 'ALM-02')!.id;
  ids.rodamiento = (await get('admin', '/api/data/Producto', { search: 'ROD-6204' })).items[0].id;
  ids.proveedor = (await get('admin', '/api/data/Proveedor', { search: 'Cartones' })).items[0].id;
  const rodamiento = (await get('admin', `/api/data/Producto/${ids.rodamiento}`)) as Row;
  for (const [key, clave, metodo] of [['promedio', 'COS-PROM', undefined], ['fifo', 'COS-FIFO', 'FIFO']] as const) {
    const p = await post('admin', '/api/data/Producto', { Clave: clave, Descripcion: `Producto ${metodo ?? 'promedio'}`, Unidad: rodamiento.Unidad,
      Categoria: rodamiento.Categoria, CostoUltimo: 10, ...(metodo ? { MetodoCosteo: metodo } : {}) });
    expect(p.status, JSON.stringify(p.body)).toBe(201);
    ids[key] = p.body.id;
  }
  // Sin indicarlo, el método es Promedio.
  expect(((await get('admin', `/api/data/Producto/${ids.promedio}`)) as Row).MetodoCosteo).toBe('Promedio');

  const alm = (await get('admin', `/api/data/Almacen/${ids.almacen1}`)) as Row;
  const upd = await put('admin', `/api/data/Almacen/${ids.almacen1}`, { _rowVersion: alm._rowVersion, CuentaInventario: cuentas['1200'], CuentaEntradas: cuentas['2150'] });
  expect(upd.status, JSON.stringify(upd.body)).toBe(200);
});

test('H1 CA2: una póliza descuadrada no se aplica y la UI dice por cuánto', async ({ browser }) => {
  const pol = await poliza([{ cuenta: '1100', cargo: 1000 }, { cuenta: '3100', abono: 900 }], { Concepto: 'Aportación de capital' });
  expect(pol).toMatchObject({ Estado: 'Borrador', Origen: 'Manual', TotalCargos: 1000, TotalAbonos: 900 }); // el borrador se guarda descuadrado
  expect(pol.Folio).toMatch(/^POL-\d{6}$/);

  const page = await pageAs(browser, 'contador');
  await page.goto(`/c/Poliza/${pol.id}`);
  await act(page, 'Aplicar póliza');
  // Regresión (revisión exploratoria 020, F-01): el diálogo solo decía «Hay datos inválidos.», sin el detalle del documento.
  await expect(page.getByRole('dialog').getByText('No cuadra: cargos 1,000.00 y abonos 900.00 (diferencia 100.00). La partida doble exige que sean iguales.')).toBeVisible();
  await expectAccessible(page); // con el diálogo y su error a la vista
  await page.getByRole('dialog').getByRole('button', { name: 'Cancelar' }).click();
  expect(((await get('contador', `/api/data/Poliza/${pol.id}`)) as Row).Estado).toBe('Borrador');

  // Corregida, se aplica desde la UI y mueve los saldos.
  const fresh = (await get('contador', `/api/data/Poliza/${pol.id}`)) as Row & { Partidas: Row[] };
  const fixed = await put('contador', `/api/data/Poliza/${pol.id}`, { _rowVersion: fresh._rowVersion,
    Partidas: [{ id: fresh.Partidas[0].id, Cuenta: cuentas['1100'], Cargo: 1000, Abono: 0 }, { id: fresh.Partidas[1].id, Cuenta: cuentas['3100'], Cargo: 0, Abono: 1000 }] });
  expect(fixed.status, JSON.stringify(fixed.body)).toBe(200);
  await page.reload();
  await act(page, 'Aplicar póliza');
  await expect(page.getByText('Documento cerrado a edición')).toBeVisible();
  await page.context().close();
  expect(await cuenta('1100')).toMatchObject({ Cargos: 1000, Abonos: 0, Saldo: 1000 });
  expect(await cuenta('3100')).toMatchObject({ Cargos: 0, Abonos: 1000, Saldo: 1000 }); // acreedora: abonos − cargos
});

test('H1 CA2: cada partida lleva cargo o abono y se necesitan dos (por API)', async () => {
  const ambos = await poliza([{ cuenta: '1100', cargo: 50, abono: 50 }, { cuenta: '3100', abono: 0 }]);
  const r1 = await transition('contador', 'Poliza', ambos, 'aplicar');
  expect(r1.status).toBe(400);
  expect(r1.body.errors!['Partidas[0].Cargo'][0]).toMatch(/un cargo o un abono \(no ambos, ni ninguno\)/);

  const una = await poliza([{ cuenta: '1100', cargo: 50 }]);
  const r2 = await transition('contador', 'Poliza', una, 'aplicar');
  expect(r2.body.errors!.Partidas[0]).toMatch(/al menos dos partidas/);

  const negativa = await poliza([{ cuenta: '1100', cargo: -50 }, { cuenta: '3100', abono: -50 }]);
  expect((await transition('contador', 'Poliza', negativa, 'aplicar')).body.errors!['Partidas[0].Cargo'][0]).toMatch(/no pueden ser negativos/);
});

test('H4 CA3: cancelar una póliza aplicada la quita de los saldos', async () => {
  const pol = await poliza([{ cuenta: '6100', cargo: 300 }, { cuenta: '1100', abono: 300 }], { Concepto: 'Papelería' });
  const applied = await transition('contador', 'Poliza', pol, 'aplicar');
  expect(applied.body.Estado).toBe('Aplicada');
  expect(await cuenta('6100')).toMatchObject({ Cargos: 300, Saldo: 300 });
  expect(await cuenta('1100')).toMatchObject({ Cargos: 1000, Abonos: 300, Saldo: 700 });

  expect((await transition('contador', 'Poliza', applied.body, 'cancelar')).status).toBe(400); // pide comentario
  const canceled = await transition('contador', 'Poliza', applied.body, 'cancelar', { comment: 'Capturada dos veces' });
  expect(canceled.body.Estado).toBe('Cancelada');
  expect(await cuenta('6100')).toMatchObject({ Cargos: 0, Saldo: 0 });
  expect(await cuenta('1100')).toMatchObject({ Cargos: 1000, Abonos: 0, Saldo: 1000 });
});

test('H1 CA3: sin periodo o con el periodo cerrado no se aplica ni se cancela; reabrir es del administrador', async ({ browser }) => {
  const futura = await poliza([{ cuenta: '1100', cargo: 10 }, { cuenta: '3100', abono: 10 }], { Fecha: NEXT_YEAR });
  const r1 = await transition('contador', 'Poliza', futura, 'aplicar');
  expect(r1.status).toBe(400);
  expect(r1.body.errors!.Fecha[0]).toMatch(/^No hay un periodo contable que incluya el 15\/06\/\d{4}: créalo en Periodos contables/);

  const aplicada = (await transition('contador', 'Poliza', await poliza([{ cuenta: '1100', cargo: 20 }, { cuenta: '3100', abono: 20 }]), 'aplicar')).body;
  const borrador = await poliza([{ cuenta: '1100', cargo: 30 }, { cuenta: '3100', abono: 30 }]);

  // El contador cierra el mes desde la UI.
  const page = await pageAs(browser, 'contador');
  await page.goto(`/c/PeriodoContable/${ids.periodo}`);
  await act(page, 'Cerrar periodo');
  await expect(page.getByText('Documento cerrado a edición')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Reabrir' })).toBeDisabled(); // solo el administrador
  await expectAccessible(page);
  await page.context().close();

  const r2 = await transition('contador', 'Poliza', borrador, 'aplicar');
  expect(r2.status).toBe(400);
  expect(r2.body.errors!.Fecha[0]).toBe(`El periodo «Mes en curso» está cerrado: no se registran, cambian ni cancelan pólizas contabilizadas con fecha del ${TODAY.split('-').reverse().join('/')}.`);
  // Cancelar lo aplicado tampoco: el diálogo con comentario explica por qué (F-01).
  const ui = await pageAs(browser, 'contador');
  await ui.goto(`/c/Poliza/${aplicada.id}`);
  await ui.getByRole('button', { name: 'Cancelar póliza' }).click();
  const dialog = ui.getByRole('dialog');
  await dialog.getByLabel(/Comentario/).fill('Error de captura');
  await dialog.getByRole('button', { name: 'Cancelar póliza' }).click();
  await expect(dialog.getByRole('alert').getByText(/El periodo «Mes en curso» está cerrado/)).toBeVisible();
  await ui.context().close();
  const r3 = await transition('contador', 'Poliza', aplicada, 'cancelar', { comment: 'Error' });
  expect(r3.status).toBe(400);
  expect(r3.body.errors!.Fecha[0]).toMatch(/está cerrado/);
  // Un borrador del periodo cerrado sí se edita (no está contabilizado): se puede pasar a otra fecha.
  const fresh = (await get('contador', `/api/data/Poliza/${borrador.id}`)) as Row;
  expect((await put('contador', `/api/data/Poliza/${borrador.id}`, { _rowVersion: fresh._rowVersion, Concepto: 'Para el siguiente mes' })).status).toBe(200);

  const periodo = (await get('contador', `/api/data/PeriodoContable/${ids.periodo}`)) as Row;
  expect((await transition('contador', 'PeriodoContable', periodo, 'reabrir', { comment: 'Ajuste' })).status).toBe(403);
  expect((await transition('admin', 'PeriodoContable', periodo, 'reabrir')).status).toBe(400); // pide comentario
  const reopened = await transition('admin', 'PeriodoContable', periodo, 'reabrir', { comment: 'Falta registrar un ajuste' });
  expect(reopened.body.Estado).toBe('Abierto');
  expect((await transition('contador', 'Poliza', (await get('contador', `/api/data/Poliza/${borrador.id}`)) as Row, 'aplicar')).body.Estado).toBe('Aplicada');
});

test('H1 CA4: dos periodos de la misma empresa no se traslapan', async () => {
  const r = await post('contador', '/api/data/PeriodoContable', { Nombre: 'Traslapado', Inicio: MONTH_END, Fin: `${now.getFullYear() + 1}-01-31` });
  expect(r.status).toBe(400);
  expect(r.body.errors!.Inicio[0]).toBe('Se traslapa con el periodo «Mes en curso»: dos periodos no pueden compartir fechas.');
  const futuro = await post('contador', '/api/data/PeriodoContable', { Nombre: 'Año próximo', Inicio: `${now.getFullYear() + 1}-01-01`, Fin: `${now.getFullYear() + 1}-12-31` });
  expect(futuro.status, JSON.stringify(futuro.body)).toBe(201);
});

test('H1 CA6: cerrar el periodo mientras se aplica una póliza no deja una aplicada después del cierre', async () => {
  const pols = await Promise.all([1, 2, 3].map((n) => poliza([{ cuenta: '1100', cargo: n }, { cuenta: '3100', abono: n }])));
  const periodo = (await get('contador', `/api/data/PeriodoContable/${ids.periodo}`)) as Row;
  const results = await Promise.all([
    ...pols.map((p) => transition('contador', 'Poliza', p, 'aplicar')),
    transition('contador', 'PeriodoContable', periodo, 'cerrar'),
  ]);
  expect(results.every((r) => r.status === 200 || r.status === 400), JSON.stringify(results.map((r) => r.status))).toBe(true);
  expect(results.at(-1)!.status).toBe(200);
  // Cada póliza quedó aplicada antes del cierre o rechazada por el cierre: nunca aplicada con el periodo ya cerrado.
  const cierre = (await get('contador', `/api/data/PeriodoContable/${ids.periodo}/history`)).at(-1);
  for (const [i, p] of pols.entries()) {
    const estado = ((await get('contador', `/api/data/Poliza/${p.id}`)) as Row).Estado;
    if (results[i].status === 400) {
      expect(estado).toBe('Borrador');
      expect(results[i].body.errors!.Fecha[0]).toMatch(/está cerrado/);
    } else {
      const aplicada = (await get('contador', `/api/data/Poliza/${p.id}/history`)).at(-1);
      expect(new Date(aplicada.at).getTime()).toBeLessThanOrEqual(new Date(cierre.at).getTime());
    }
  }
  await transition('admin', 'PeriodoContable', (await get('admin', `/api/data/PeriodoContable/${ids.periodo}`)) as Row, 'reabrir', { comment: 'Sigue el mes' });
});

test('H2 CA1 · CA2: promedio ponderado — las entradas se funden y la salida toma el promedio', async () => {
  const e1 = await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.promedio, Cantidad: 10, CostoUnitario: 10 });
  expect(e1).toMatchObject({ CostoTotal: 100, CostoUnitario: 10 });
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.promedio, Cantidad: 10, CostoUnitario: 13 });
  expect(await existencia(ids.almacen1, ids.promedio)).toMatchObject({ Cantidad: 20, Valor: 230, CostoPromedio: 11.5 });

  const s = await movimiento('almacen', { Tipo: 'Salida', Almacen: ids.almacen1, Producto: ids.promedio, Cantidad: 3 });
  expect(s).toMatchObject({ CostoTotal: -34.5, CostoUnitario: 11.5 });
  expect(await existencia(ids.almacen1, ids.promedio)).toMatchObject({ Cantidad: 17, Valor: 195.5, CostoPromedio: 11.5 });
});

test('H2 CA1: FIFO consume la capa más antigua primero', async () => {
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.fifo, Cantidad: 10, CostoUnitario: 10 });
  await movimiento('almacen', { Tipo: 'Entrada', Almacen: ids.almacen1, Producto: ids.fifo, Cantidad: 10, CostoUnitario: 13 });
  const s = await movimiento('almacen', { Tipo: 'Salida', Almacen: ids.almacen1, Producto: ids.fifo, Cantidad: 12 });
  expect(s).toMatchObject({ CostoTotal: -126, CostoUnitario: 10.5 }); // 10 × 10 + 2 × 13
  expect(await existencia(ids.almacen1, ids.fifo)).toMatchObject({ Cantidad: 8, Valor: 104, CostoPromedio: 13 });
});

test('H2 CA3: entrada sin costo al promedio vigente; salida de existencias previas al costo de respaldo', async () => {
  const ajuste = await movimiento('supervisor', { Tipo: 'Ajuste', Almacen: ids.almacen1, Producto: ids.promedio, Cantidad: 2, Notas: 'Sobrante' });
  expect(ajuste).toMatchObject({ CostoTotal: 23, CostoUnitario: 11.5 });
  expect(await existencia(ids.almacen1, ids.promedio)).toMatchObject({ Cantidad: 19, Valor: 218.5, CostoPromedio: 11.5 });

  // El rodamiento tenía existencia desde antes de instalar Costeo (sin capas): sale al costo último y el valor no queda negativo.
  const producto = (await get('admin', `/api/data/Producto/${ids.rodamiento}`)) as Row;
  const antes = (await existencia(ids.almacen1, ids.rodamiento))!;
  expect(antes.Valor ?? 0).toBe(0); // vacío: la existencia es anterior a Costeo y aún no se mueve
  // Regresión (revisión exploratoria 020, F-02): sin valor, el costo promedio salía «0.00», como si no costara nada.
  expect(antes.CostoPromedio).toBeNull();
  const s = await movimiento('almacen', { Tipo: 'Salida', Almacen: ids.almacen1, Producto: ids.rodamiento, Cantidad: 2 });
  expect(s.CostoTotal).toBe(-Math.round(2 * (producto.CostoUltimo as number) * 100) / 100);
  expect(await existencia(ids.almacen1, ids.rodamiento)).toMatchObject({ Cantidad: (antes.Cantidad as number) - 2, Valor: 0 });
});

test('H2 CA4 · H5 CA1: un traspaso entra al destino al costo de origen', async () => {
  const tra = await post('almacen', '/api/data/Traspaso', { AlmacenOrigen: ids.almacen1, AlmacenDestino: ids.almacen2, Partidas: [{ Producto: ids.fifo, Cantidad: 4 }] });
  expect(tra.status, JSON.stringify(tra.body)).toBe(201);
  expect((await transition('almacen', 'Traspaso', tra.body, 'aplicar')).body.Estado).toBe('Aplicado');
  const movs = (await get('almacen', '/api/data/MovimientoInventario', { search: tra.body.Folio as string, pageSize: 10 })).items as Row[];
  expect(movs.map((m) => [m.Tipo, m.CostoTotal, m.CostoUnitario]).sort()).toEqual([['Traspaso entrada', 52, 13], ['Traspaso salida', -52, 13]]);
  expect(await existencia(ids.almacen1, ids.fifo)).toMatchObject({ Cantidad: 4, Valor: 52 });
  expect(await existencia(ids.almacen2, ids.fifo)).toMatchObject({ Cantidad: 4, Valor: 52 });
});

test('H3 · H5 CA1 · CA3: recepción en euros → entrada en moneda base con CONVERT y póliza aplicada', async ({ browser }) => {
  const monedas = (await get('admin', '/api/data/Moneda', { pageSize: 50 })).items as Row[];
  ids.eur = monedas.find((m) => m.Clave === 'EUR')?.id ?? (await post('admin', '/api/data/Moneda', { Clave: 'EUR', Nombre: 'Euro', Simbolo: '€' })).body.id;
  expect((await (await as('admin')).put('/api/admin/exchange-rates', { data: { from: 'EUR', to: 'MXN', date: TODAY, rate: 20 } })).status()).toBe(200);
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen1, Moneda: ids.eur,
    Partidas: [{ Producto: ids.promedio, Cantidad: 5, PrecioUnitario: 2, DescuentoPct: 0 }] });
  expect(oc.TipoCambio).toBe(20);
  // El euro sube antes de recibir: el inventario entra al tipo de la fecha de la recepción, no al de la orden.
  expect((await (await as('admin')).put('/api/admin/exchange-rates', { data: { from: 'EUR', to: 'MXN', date: TODAY, rate: 21 } })).status()).toBe(200);

  const rec = await post('almacen', '/api/data/Recepcion', { OrdenCompra: oc.id, Factura: 'F-COS-1', Partidas: [{ PartidaOrden: (oc.Partidas as Row[])[0].id }] });
  expect(rec.status, JSON.stringify(rec.body)).toBe(201);
  expect(rec.body).toMatchObject({ Moneda: 'EUR', CostoTotal: 210 });
  expect((rec.body.Partidas as Row[])[0]).toMatchObject({ CostoUnitario: 42, Costo: 210 });
  const confirmed = await transition('almacen', 'Recepcion', rec.body, 'confirmar');
  expect(confirmed.status, JSON.stringify(confirmed.body)).toBe(200);

  const entrada = ((await get('almacen', '/api/data/MovimientoInventario', { search: rec.body.Folio as string })).items as Row[])[0];
  expect(entrada).toMatchObject({ Tipo: 'Entrada', Cantidad: 5, CostoUnitario: 42, CostoTotal: 210 });
  expect(await existencia(ids.almacen1, ids.promedio)).toMatchObject({ Cantidad: 24, Valor: 428.5 }); // 218.50 + 210

  const pols = (await get('contador', '/api/data/Poliza', { search: rec.body.Folio as string })).items as Row[];
  expect(pols).toHaveLength(1);
  expect(pols[0]).toMatchObject({ Estado: 'Aplicada', Origen: 'Automática', Referencia: rec.body.Folio, TotalCargos: 210, TotalAbonos: 210 });
  expect(await cuenta('1200')).toMatchObject({ Cargos: 210, Saldo: 210 });
  expect(await cuenta('2150')).toMatchObject({ Abonos: 210, Saldo: 210 });
  const history = await get('contador', `/api/data/Poliza/${pols[0].id}/history`);
  expect(history.at(-1).comment).toBe(`Automática: Póliza de entrada (Recepción ${rec.body.Folio}).`);
  // Regresión (spec 021, H6): cancelar a mano una póliza automática aplicada la quitaba de los saldos y dejaba el inventario
  // valuado sin su póliza (contabilidad e inventario descuadrados).
  const cancel = await transition('contador', 'Poliza', await get('contador', `/api/data/Poliza/${pols[0].id}`), 'cancelar', { comment: 'Intento' });
  expect(cancel.status).toBe(400);
  expect(JSON.stringify(cancel.body)).toContain('Una póliza automática aplicada no se cancela');
  expect(await cuenta('1200')).toMatchObject({ Cargos: 210, Saldo: 210 });

  // El contador la ve cerrada a edición, con sus dos partidas.
  const page = await pageAs(browser, 'contador');
  await page.goto(`/c/Poliza/${pols[0].id}`);
  await expect(page.getByText('Documento cerrado a edición')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Concepto' })).toHaveValue(`Entrada por recepción ${rec.body.Folio}`);
  await expect(page.getByRole('cell', { name: `Entradas por facturar · ${rec.body.Folio}` })).toBeVisible();
  await expect(page.getByText(/1200 · Inventario de mercancías/).first()).toBeVisible();
  await expectAccessible(page);
  await page.context().close();
});

test('H3 CA2: si la póliza automática no se puede aplicar, la recepción no se confirma', async () => {
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen1, Partidas: [{ Producto: ids.fifo, Cantidad: 3, PrecioUnitario: 10, DescuentoPct: 0 }] });
  const rec = (await post('almacen', '/api/data/Recepcion', { OrdenCompra: oc.id, Factura: 'F-COS-2', Fecha: `${now.getFullYear() + 1}-02-10`,
    Partidas: [{ PartidaOrden: (oc.Partidas as Row[])[0].id }] })).body;
  // El periodo del año próximo (creado arriba) se cierra para provocar el rechazo.
  const futuro = ((await get('contador', '/api/data/PeriodoContable', { search: 'Año próximo' })).items as Row[])[0];
  expect((await transition('contador', 'PeriodoContable', futuro, 'cerrar')).status).toBe(200);
  const failed = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(failed.status).toBe(400);
  expect(failed.body.title).toMatch(/^No se pudo completar «Póliza de entrada»: El periodo «Año próximo» está cerrado/);
  // Todo se revirtió: sin entrada al almacén ni póliza, y la recepción sigue en borrador.
  expect(((await get('almacen', `/api/data/Recepcion/${rec.id}`)) as Row).Estado).toBe('Borrador');
  expect((await get('almacen', '/api/data/MovimientoInventario', { search: rec.Folio as string })).items).toHaveLength(0);
  expect((await get('contador', '/api/data/Poliza', { search: rec.Folio as string })).items).toHaveLength(0);
});

test('H5 CA3: un almacén sin cuentas no confirma una recepción con costo (política por defecto de la 022)', async () => {
  // Spec 022 (H7): en la 020 se confirmaba y se valuaba sin póliza, sin dejar rastro. Ahora la regla `PolizaConCuentas` de
  // Costeo 1.1.0 lo impide; la alternativa (quitarla: queda «póliza pendiente») se prueba en valuacion-politicas.spec.ts.
  const oc = await ordenEnviada({ Proveedor: ids.proveedor, Almacen: ids.almacen2, Partidas: [{ Producto: ids.fifo, Cantidad: 2, PrecioUnitario: 15, DescuentoPct: 0 }] });
  const rec = (await post('almacen', '/api/data/Recepcion', { OrdenCompra: oc.id, Factura: 'F-COS-3', Partidas: [{ PartidaOrden: (oc.Partidas as Row[])[0].id }] })).body;
  const blocked = await transition('almacen', 'Recepcion', rec, 'confirmar');
  expect(blocked.status).toBe(400);
  expect(blocked.body.errors!.Almacen[0]).toMatch(/no tiene la cuenta de inventario/);
  expect((await get('almacen', '/api/data/MovimientoInventario', { search: rec.Folio as string })).items).toHaveLength(0);
  expect(await existencia(ids.almacen2, ids.fifo)).toMatchObject({ Cantidad: 4, Valor: 52 });
});

test('H2 CA5: las capas son por empresa', async () => {
  const admin = await apiAs(ADMIN.username, ADMIN.password); // sesión propia: cambiar de empresa no afecta a las demás pruebas
  const created = await admin.post('/api/admin/companies', { data: { code: 'e2e-costos', name: 'Costos E2E', baseCurrency: 'MXN' } });
  expect(created.status(), await created.text()).toBe(200);
  const company = await created.json();
  expect((await admin.post('/api/auth/company', { data: { companyId: company.id } })).status()).toBe(200);
  const alm = await (await admin.post('/api/data/Almacen', { data: { Clave: 'ALM-01', Nombre: 'Almacén de la otra empresa' } })).json();
  const base = { Almacen: alm.id, Producto: ids.fifo };
  expect((await admin.post('/api/data/MovimientoInventario', { data: { ...base, Tipo: 'Entrada', Cantidad: 1, CostoUnitario: 99 } })).status()).toBe(201);
  const salida = await (await admin.post('/api/data/MovimientoInventario', { data: { ...base, Tipo: 'Salida', Cantidad: 1 } })).json();
  expect(salida.CostoTotal).toBe(-99); // sus propias capas, no las de la empresa principal (13.00)
  await admin.dispose();
  expect(await existencia(ids.almacen1, ids.fifo)).toMatchObject({ Cantidad: 4, Valor: 52 }); // sin cambios
});

test('H1 CA5 · H2 CA6 · H3 CA3: libro, valuación y póliza automática mal declarados se rechazan en la vista previa', async () => {
  const admin = await as('admin');
  const preview = async (operations: object[]) => (await (await admin.post('/api/model/plan/preview', { data: { description: 'mal', operations } })).json()) as
    { valid: boolean; errors: { message: string }[] };
  const ledger = await preview([{ op: 'setLedger', entity: 'Poliza', ledger: { lines: 'Partidas', debit: 'Concepto', credit: 'Abono', states: ['Contabilizada'] } }]);
  expect(ledger.valid).toBe(false);
  expect(ledger.errors.map((e) => e.message)).toEqual(expect.arrayContaining([
    expect.stringMatching(/libro: el cargo 'Concepto' debe ser un campo numérico/), expect.stringMatching(/libro: el estado 'Contabilizada' no existe/)]));

  const cost = await preview([{ op: 'setAutomations', entity: 'MovimientoInventario', merge: true, automations: [{ name: 'Valuar', label: 'Valuar', on: 'create',
    actions: [{ type: 'cost', entity: 'Existencia', match: [{ field: 'Almacen', source: 'Almacen' }], field: 'Valor', quantity: 'Efecto', method: "'lifo'", costField: 'Efecto' }] }] }]);
  expect(cost.errors.map((e) => e.message)).toEqual(expect.arrayContaining([
    expect.stringMatching(/método 'lifo' no válido/), expect.stringMatching(/'Efecto' debe ser un campo numérico capturable/)]));

  const lines = await preview([{ op: 'setAutomations', entity: 'Recepcion', merge: true, automations: [{ name: 'PolizaEntrada', label: 'Póliza', on: 'enterState', state: 'Confirmada',
    actions: [{ type: 'create', entity: 'Poliza', set: [{ field: 'Concepto', value: 'Folio' }], createLines: [{ field: 'Partidas', set: [{ field: 'Cargo', value: '1' }] }], transition: 'contabilizar' }] }] }]);
  expect(lines.errors.map((e) => e.message)).toEqual(expect.arrayContaining([
    expect.stringMatching(/partida 1: falta asignar 'Cuenta'/), expect.stringMatching(/la transición 'contabilizar' no existe en Poliza/)]));
});

test('H4 CA5: balanza de comprobación y tarea de cierre en pausa', async ({ browser }) => {
  const balanza = await get('contador', '/api/reports/CuentaContable/Balanza');
  const activo = balanza.rows.find((r: Record<string, unknown>) => r.g0 === 'Activo');
  // Cargos del activo: bancos e inventario (clientes no se movió).
  expect(activo!.m0).toBe(((await cuenta('1100')).Cargos as number) + ((await cuenta('1200')).Cargos as number));
  const schedules = (await get('admin', '/api/admin/automations/schedules')) as { entity: string; name: string; active: boolean; role: string }[];
  expect(schedules.find((s) => s.entity === 'PeriodoContable' && s.name === 'CerrarVencidos')).toMatchObject({ active: false, role: 'Cierre contable' });

  const page = await pageAs(browser, 'contador');
  await page.goto('/c/CuentaContable');
  await expect(page.getByRole('cell', { name: 'Inventario de mercancías' })).toBeVisible();
  await expectAccessible(page);
  await page.context().close();
});
