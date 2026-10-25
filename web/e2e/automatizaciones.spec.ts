// Spec 005 · H1: automatizaciones declarativas del motor, probadas con un modelo propio (sin depender de
// Compras). El modelo se prepara por API; las automatizaciones se declaran desde el modelador (CA7).
import type { APIRequestContext } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, openJson, test } from './helpers';

test.describe.configure({ mode: 'serial' });

const G = 'Pruebas 005';
let admin: APIRequestContext;

const model = {
  description: 'Spec 005 · modelo de prueba',
  operations: [
    { op: 'createEntity', group: G, name: 'Proyecto', label: 'Proyecto', labelPlural: 'Proyectos', displayField: 'Nombre', fields: [
      { name: 'Nombre', label: 'Nombre', type: 'text', required: true },
      { name: 'Horas', label: 'Horas', type: 'decimal', editable: false, defaultValue: '0' },
      { name: 'UltimaNota', label: 'Última nota', type: 'text', editable: false },
    ] },
    { op: 'setRules', entity: 'Proyecto', rules: [
      { name: 'Presupuesto', field: 'Horas', assert: 'Horas <= 60', message: 'El proyecto excede su presupuesto de 60 horas.' },
    ] },
    { op: 'setWorkflow', entity: 'Proyecto', workflow: {
      initial: 'Abierto',
      states: [{ name: 'Abierto', label: 'Abierto', color: 'accent' }, { name: 'Cerrado', label: 'Cerrado', color: 'success', editable: false }],
      transitions: [{ name: 'cerrar', label: 'Cerrar', from: ['Abierto'], to: 'Cerrado', system: true }],
    } },
    { op: 'createEntity', group: G, name: 'ParteHoras', label: 'Parte de horas', labelPlural: 'Partes de horas', fields: [
      { name: 'Proyecto', label: 'Proyecto', type: 'reference', referenceEntity: 'Proyecto', required: true },
      { name: 'Horas', label: 'Horas', type: 'decimal', required: true },
      { name: 'Nota', label: 'Nota', type: 'text' },
    ] },
    { op: 'setWorkflow', entity: 'ParteHoras', workflow: {
      initial: 'Borrador',
      states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Aprobado', label: 'Aprobado', color: 'success', editable: false }],
      transitions: [{ name: 'aprobar', label: 'Aprobar', from: ['Borrador'], to: 'Aprobado', variant: 'primary' }],
    } },
    { op: 'createEntity', group: G, name: 'Aviso', label: 'Aviso', labelPlural: 'Avisos', fields: [
      { name: 'Mensaje', label: 'Mensaje', type: 'text', required: true, maxLength: 300 },
    ] },
  ],
};

// Los cuatro eventos (CA1) y las cuatro acciones (CA2).
const automations = [
  { name: 'AcumularHoras', label: 'Acumular horas en el proyecto', on: 'enterState', state: 'Aprobado', actions: [
    { type: 'rollup', target: 'Proyecto', field: 'Horas', sum: 'Horas', filter: "Estado = 'Aprobado'" },
    { type: 'transition', target: 'Proyecto', transition: 'cerrar', when: 'Proyecto.Horas >= 40' },
  ] },
  { name: 'UltimaNota', label: 'Copiar la nota al proyecto', on: 'transition', transition: 'aprobar', actions: [
    { type: 'update', target: 'Proyecto', when: 'NOT ISBLANK(Nota)', set: [{ field: 'UltimaNota', value: 'Nota' }] },
  ] },
  { name: 'AvisarParteGrande', label: 'Avisar partes grandes', on: 'create', when: 'Horas > 20', actions: [
    { type: 'create', entity: 'Aviso', set: [{ field: 'Mensaje', value: "'Parte de ' & Horas & ' horas: ' & COALESCE(Nota, 'sin nota')" }] },
  ] },
  { name: 'AvisarEdicion', label: 'Avisar ediciones', on: 'update', actions: [
    { type: 'create', entity: 'Aviso', set: [{ field: 'Mensaje', value: "'Se editó el parte: ' & COALESCE(Nota, 'sin nota')" }] },
  ] },
];

type Row = { id: number; _rowVersion: string; [k: string]: unknown };
let proyecto: Row;

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- respuestas JSON de la API, se verifican campo por campo
async function json(res: Awaited<ReturnType<APIRequestContext["get"]>>): Promise<{ status: number; body: any }> {
  return { status: res.status(), body: await res.json() };
}
const get = async (entity: string, id: number) => (await admin.get(`/api/data/${entity}/${id}`)).json();
const create = async (entity: string, data: object) => json(await admin.post(`/api/data/${entity}`, { data }));
const transition = async (entity: string, row: Row, name: string) =>
  json(await admin.post(`/api/data/${entity}/${row.id}/transition`, { data: { transition: name, _rowVersion: row._rowVersion } }));
const avisos = async (search: string) =>
  (await (await admin.get('/api/data/Aviso', { params: { search } })).json()).items.map((r: { Mensaje: string }) => r.Mensaje);

test('preparar el modelo de prueba', async () => {
  admin = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(admin, model);
});

test('H1 CA4 · CA7: el modelador declara automatizaciones como JSON validado en la vista previa', async ({ page }) => {
  // Spec 026: las automatizaciones tienen su pestaña y el JSON es la opción avanzada plegada de la sección.
  await page.goto('/admin/model?edit=ParteHoras&tab=automatizaciones');
  const box = await openJson(page, 'Automatizaciones (JSON)');

  // JSON mal formado: lo detecta el cliente.
  await box.fill('[ { "name": ');
  // Spec 026: el error se ve en el JSON y en el editor visual (que no lo pisa).
  await expect(page.getByText(/JSON inválido/).first()).toBeVisible();
  await expect(page.getByText('El JSON de esta sección no es válido')).toBeVisible();

  // JSON bien formado pero con un campo que no existe: lo rechaza la vista previa del servidor.
  const broken = [{ ...automations[0], actions: [{ ...automations[0].actions[0], field: 'NoExiste' }] }];
  await box.fill(JSON.stringify(broken, null, 2));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan tiene errores')).toBeVisible();
  await expect(dialog.getByText(/'NoExiste' debe ser un campo numérico/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Volver a editar' }).click();

  await box.fill(JSON.stringify(automations, null, 2));
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText(/Definir 4 automatización\(es\) en 'Parte de horas'/)).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();

  const def = await (await admin.get('/api/model/entities/ParteHoras')).json();
  expect(def.automations.map((a: { name: string }) => a.name)).toEqual(automations.map((a) => a.name));

  // Regresión (F-02, revisión exploratoria 005): el editor mostraba propiedades "null" que nadie escribió.
  await page.goto('/admin/model?edit=ParteHoras&tab=automatizaciones');
  await expect(await openJson(page, 'Automatizaciones (JSON)')).toHaveValue(/"AvisarEdicion"/);
  await expect(page.getByLabel('Automatizaciones (JSON)')).not.toHaveValue(/null/);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeVisible();
});

test('H1 CA1 · CA2: crear y editar disparan sus automatizaciones; aprobar acumula y copia la nota', async () => {
  proyecto = (await create('Proyecto', { Nombre: 'Portal de clientes' })).body;
  expect(proyecto.Horas).toBe(0);

  const p1 = await create('ParteHoras', { Proyecto: proyecto.id, Horas: 25, Nota: 'Diseño' });
  expect(p1.status).toBe(201);
  expect(await avisos('Diseño')).toEqual(['Parte de 25.00 horas: Diseño']); // al crear, con condición
  expect(await avisos('sin nota')).toEqual([]);

  const edited = await json(await admin.put(`/api/data/ParteHoras/${p1.body.id}`, { data: { _rowVersion: p1.body._rowVersion, Nota: 'Diseño UX' } }));
  expect(await avisos('Se editó')).toEqual(['Se editó el parte: Diseño UX']); // al editar

  expect((await transition('ParteHoras', edited.body, 'aprobar')).body.Estado).toBe('Aprobado');
  const after = await get('Proyecto', proyecto.id);
  expect(after.Horas).toBe(25);                 // acumular (al entrar al estado)
  expect(after.UltimaNota).toBe('Diseño UX');   // asignar campos (al ejecutar la transición)
  expect(after.Estado).toBe('Abierto');

  const p2 = await create('ParteHoras', { Proyecto: proyecto.id, Horas: 10 });
  await transition('ParteHoras', p2.body, 'aprobar');
  expect((await get('Proyecto', proyecto.id)).Horas).toBe(35);
  // Un parte en borrador no cuenta (filtro del acumulado).
  await create('ParteHoras', { Proyecto: proyecto.id, Horas: 3 });
  expect((await get('Proyecto', proyecto.id)).Horas).toBe(35);
});

test('H1 CA3: si una acción falla, se revierte todo y el mensaje nombra la automatización', async () => {
  const p = await create('ParteHoras', { Proyecto: proyecto.id, Horas: 30, Nota: 'Excedente' });
  const failed = await transition('ParteHoras', p.body, 'aprobar');
  expect(failed.status).toBe(400);
  expect(failed.body.title).toBe('No se pudo completar «Acumular horas en el proyecto»: El proyecto excede su presupuesto de 60 horas.');

  // Nada quedó a medias: ni el parte, ni las horas, ni la nota copiada por la otra automatización.
  expect((await get('ParteHoras', p.body.id)).Estado).toBe('Borrador');
  const proj = await get('Proyecto', proyecto.id);
  expect(proj.Horas).toBe(35);
  expect(proj.UltimaNota).toBe('Diseño UX');
});

test('H1 CA2 · CA6: una transición del sistema cierra el proyecto, queda en historial y auditoría, y nadie la ejecuta a mano', async ({ page }) => {
  const p = await create('ParteHoras', { Proyecto: proyecto.id, Horas: 5 });
  await transition('ParteHoras', p.body, 'aprobar');
  const proj = await get('Proyecto', proyecto.id);
  expect(proj.Horas).toBe(40);
  expect(proj.Estado).toBe('Cerrado');

  const history = await (await admin.get(`/api/data/Proyecto/${proyecto.id}/history`)).json();
  expect(history.at(-1).comment).toMatch(/^Automática: Acumular horas en el proyecto \(Parte de horas #\d+\)\.$/);
  const audit = await (await admin.get('/api/audit', { params: { entity: 'Proyecto', action: 'automation.update' } })).json();
  expect(audit.total).toBeGreaterThanOrEqual(3);
  expect(audit.items[0].user).toBe('admin'); // a nombre de quien disparó el evento

  // Ni siquiera el administrador ejecuta por API una transición reservada al sistema.
  const open = (await create('Proyecto', { Nombre: 'Otro' })).body;
  expect(open._workflow.transitions).toEqual([]);
  const manual = await transition('Proyecto', open, 'cerrar');
  expect(manual.status).toBe(400);

  await page.goto(`/c/Proyecto/${proyecto.id}`);
  await expect(page.getByText(/Automática: Acumular horas en el proyecto/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Cerrar', exact: true })).toHaveCount(0);
});

test('H1 CA4 · CA5: los ciclos se rechazan al compilar y las cadenas largas se cortan a los 5 niveles', async () => {
  // Cadena1 → Cadena2 → … → Cadena6 (sin ciclo): cada alta crea la siguiente.
  const ops: object[] = [];
  for (let i = 1; i <= 6; i++) {
    ops.push({ op: 'createEntity', group: G, name: `Cadena${i}`, label: `Cadena ${i}`, labelPlural: `Cadenas ${i}`, fields: [{ name: 'Nota', label: 'Nota', type: 'text' }] });
  }
  const step = (i: number, entity: string) => ({ op: 'setAutomations', entity: `Cadena${i}`, automations: [
    { name: 'Siguiente', label: `Crear después de la cadena ${i}`, on: 'create', actions: [{ type: 'create', entity, set: [{ field: entity === 'Aviso' ? 'Mensaje' : 'Nota', value: 'Nota' }] }] },
  ] });
  for (let i = 1; i <= 5; i++) ops.push(step(i, `Cadena${i + 1}`));
  ops.push(step(6, 'Aviso'));

  // Cerrar el círculo (Cadena6 → Cadena1) se rechaza en la vista previa.
  const cyclic = await (await admin.post('/api/model/plan/preview', { data: { description: 'ciclo', operations: [...ops.slice(0, -1), step(6, 'Cadena1')] } })).json();
  expect(cyclic.valid).toBe(false);
  expect(cyclic.errors.map((e: { message: string }) => e.message).join(' ')).toMatch(/Ciclo de automatizaciones: Cadena1 → Cadena2 → .* → Cadena1/);

  await applyPlan(admin, { description: 'Cadena de prueba', operations: ops });
  // Cinco niveles con automatizaciones (Cadena2 … Cadena6): se completa.
  expect((await create('Cadena2', { Nota: 'cadena corta' })).status).toBe(201);
  expect(await avisos('cadena corta')).toEqual(['cadena corta']);
  // Seis niveles (Cadena1 … Cadena6): se corta y no queda nada.
  const deep = await create('Cadena1', { Nota: 'cadena larga' });
  expect(deep.status).toBe(400);
  expect(deep.body.title).toMatch(/más de 5 niveles/);
  expect((await (await admin.get('/api/data/Cadena1')).json()).total).toBe(0);
  expect(await avisos('cadena larga')).toEqual([]);
});

// Regresión (spec 005): al agregar una fórmula, los registros que ya existían quedaban con la columna vacía.
test('Fórmulas nuevas se calculan también en los registros existentes', async () => {
  await applyPlan(admin, { description: 'Resumen del proyecto', operations: [
    { op: 'addField', entity: 'Proyecto', field: { name: 'Resumen', label: 'Resumen', type: 'formula', resultType: 'text', formula: "Nombre & ' · ' & Horas & ' h'" } },
  ] });
  expect((await get('Proyecto', proyecto.id)).Resumen).toBe('Portal de clientes · 40.00 h');
});

test('H3 CA6: Inventario requiere Compras y no se instala sin él', async ({ page }) => {
  const mods = await (await admin.get('/api/admin/modules')).json();
  const inv = mods.find((m: { id: string }) => m.id === 'inventario');
  expect(inv.requires).toEqual(['Compras']);
  expect(inv.missingRequirements).toEqual(['Compras']);
  const res = await admin.post('/api/admin/modules/inventario/install');
  expect(res.status()).toBe(409);
  expect((await res.json()).title).toMatch(/Requiere el módulo Compras/);

  await page.goto('/admin/modules');
  await expect(page.getByText('Requiere Compras')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Instalar Inventario' })).toBeDisabled();
});
