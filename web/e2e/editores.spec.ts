// Spec 019 · Editores visuales: diseñador del formulario (layout de la 007) y diagrama del proceso (002). No hay API
// nueva: lo que se prueba es que la UI produce el mismo Plan de Cambios, con ratón y con teclado, y que es accesible.
// Los catálogos de apoyo se declaran por plan; lo que se diseña, por la UI.
import type { APIRequestContext, Locator, Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Spec 019';
const PWD = 'frase larga de editores 2026';
const READER = 'e2e-019-lector';

let admin: APIRequestContext;
const entity = async (name: string) => (await (await admin.get(`/api/model/entities/${name}`)).json());

/** Un elemento al vuelo debe verlo una persona (lección de compras.spec.ts, F-20). */
async function expectHumanVisible(el: Locator) {
  await expect(el).toBeInViewport();
  const ok = await el.evaluate((n) => {
    const r = n.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return hit === n || n.contains(hit);
  });
  expect(ok, 'el elemento debe verse sin que otro lo tape').toBe(true);
}

/** Plan que la UI manda a la vista previa del servidor (H3 CA1: es el mismo Plan de Cambios de siempre). */
async function reviewPlan(page: Page): Promise<{ operations: { op: string }[] }> {
  const req = page.waitForRequest((r) => r.url().endsWith('/api/model/plan/preview') && r.method() === 'POST');
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  return (await req).postDataJSON();
}

const chip = (page: Page, label: string) => page.getByRole('button', { name: new RegExp(`^${label}(,|$)`) });
const section = (page: Page, label: string) => page.getByRole('region', { name: `Sección ${label}` });
const preview = (page: Page) => page.getByRole('region', { name: 'Vista previa' });
const live = (page: Page) => page.locator('[aria-live="assertive"]');
/** Un interruptor se acciona con un clic en su etiqueta, como lo hace una persona (el input está oculto bajo la pista). */
async function toggle(scope: Page | Locator, name: string | RegExp, on: boolean) {
  const sw = scope.getByRole('switch', { name });
  if ((await sw.isChecked()) !== on) await scope.locator('label.switch').filter({ hasText: name }).click();
  await expect(sw).toBeChecked({ checked: on });
}

test('preparar: catálogo sin layout, documento con proceso y partidas', async () => {
  admin = await apiAs(ADMIN.username, ADMIN.password);
  // Una persona sin permiso de modelador (solo el rol base).
  const res = await admin.post('/api/admin/users', { data: { username: READER, displayName: 'Lector 019', password: PWD, roleIds: [await roleId(admin, 'Usuario')] } });
  expect(res.status(), await res.text()).toBe(201);
  await applyPlan(admin, { description: 'Spec 019 · apoyo', operations: [
    { op: 'createEntity', group: GROUP, name: 'Visita', label: 'Visita', labelPlural: 'Visitas', icon: 'map-pin', fields: [
      { name: 'Cliente', label: 'Cliente', type: 'text', required: true },
      { name: 'Fecha', label: 'Fecha', type: 'date' },
      { name: 'Motivo', label: 'Motivo', type: 'choice', options: ['Venta', 'Soporte'] },
      { name: 'Contacto', label: 'Contacto', type: 'text' },
      { name: 'Notas', label: 'Notas', type: 'longtext' },
      { name: 'Monto', label: 'Monto', type: 'decimal' },
    ] },
    { op: 'createEntity', group: GROUP, name: 'Tramite', label: 'Trámite', labelPlural: 'Trámites', icon: 'file-text', fields: [
      { name: 'Asunto', label: 'Asunto', type: 'text', required: true },
      { name: 'Motivo', label: 'Motivo de rechazo', type: 'text' },
    ] },
    { op: 'createEntity', name: 'TramitePartida', label: 'Partida de trámite', labelPlural: 'Partidas de trámite', fields: [
      { name: 'Tramite', label: 'Trámite', type: 'parent', referenceEntity: 'Tramite' },
      { name: 'Concepto', label: 'Concepto', type: 'text', required: true },
    ] },
    { op: 'addField', entity: 'Tramite', field: { name: 'Partidas', label: 'Partidas', type: 'lines', childEntity: 'TramitePartida' } },
    { op: 'setWorkflow', entity: 'Tramite', workflow: {
      initial: 'Borrador',
      states: [
        { name: 'Borrador', label: 'Borrador', color: 'neutral' },
        { name: 'Enviado', label: 'Enviado', color: 'accent', editable: false },
        { name: 'Aprobado', label: 'Aprobado', color: 'success', editable: false },
        { name: 'Rechazado', label: 'Rechazado', color: 'danger', editable: false },
      ],
      transitions: [
        // Etiqueta larga a propósito: no debe tapar los estados vecinos (F-03).
        { name: 'enviar', label: 'Enviar a revisión del comité', from: ['Borrador'], to: 'Enviado', variant: 'primary' },
        { name: 'aprobar', label: 'Aprobar', from: ['Enviado'], to: 'Aprobado' },
        { name: 'rechazar', label: 'Rechazar', from: ['Enviado'], to: 'Rechazado', requireComment: true },
        { name: 'reabrir', label: 'Reabrir', from: ['Rechazado'], to: 'Borrador', system: true },
      ],
    } },
  ] });
});

test('H1 CA1 · CA2 · CA5 · CA6 · H3 CA1: crear el layout, arrastrar con el ratón, vista previa en vivo y mismo setLayout', async ({ page }) => {
  await page.goto('/admin/model?edit=Visita');
  await page.getByRole('tab', { name: 'Formulario' }).click();
  await expect(page).toHaveURL(/tab=formulario/);
  await expect(page.getByText('El formulario muestra los campos en el orden de la lista')).toBeVisible();
  // Sin layout la vista previa ya muestra el formulario actual (los campos en orden).
  await expect(preview(page).getByText('Cliente')).toBeVisible();
  await page.getByRole('button', { name: 'Crear layout' }).click();
  await expect(section(page, 'Datos generales')).toBeVisible();
  await expect(page.getByRole('complementary', { name: /Sin colocar/ }).getByText('Todos los campos están en el formulario.')).toBeVisible();

  // Vista previa en vivo: cambia con cada edición, sin guardar nada.
  await page.getByLabel('Título de la sección 1').fill('Datos de la visita');
  await expect(preview(page).getByRole('heading', { name: 'Datos de la visita' })).toBeVisible();
  // Regresión (revisión exploratoria 019, F-01): sin etiqueta, el input del título medía 100 % y bajaba de renglón,
  // separado de su asa y de sus botones.
  const center = async (l: Locator) => { const b = (await l.boundingBox())!; return b.y + b.height / 2; };
  const head = section(page, 'Datos de la visita');
  expect(Math.abs(await center(head.locator('.ld-grip')) - await center(page.getByLabel('Título de la sección 1')))).toBeLessThan(6);
  expect(Math.abs(await center(head.getByRole('button', { name: /^Quitar sección/ })) - await center(page.getByLabel('Título de la sección 1')))).toBeLessThan(6);
  await page.getByRole('button', { name: 'Agregar sección' }).click();
  // Regresión (revisión exploratoria 019, F-02): el título de la sección nueva queda seleccionado; escribir lo reemplaza.
  await expect(page.getByLabel('Título de la sección 2')).toBeFocused();
  await page.keyboard.type('Seguimiento');
  await expect(page.getByLabel('Título de la sección 2')).toHaveValue('Seguimiento');

  // Arrastrar y soltar nativo: Notas y Monto a la sección nueva (Monto antes de Notas).
  const target = section(page, 'Seguimiento').getByRole('list', { name: 'Campos de Seguimiento' });
  // Fuente y destino a la vista antes de arrastrar (una persona no arrastra hacia lo que no ve).
  await target.scrollIntoViewIfNeeded();
  await expectHumanVisible(chip(page, 'Notas'));
  await expectHumanVisible(target);
  await chip(page, 'Notas').dragTo(target);
  await expect(target.getByRole('button', { name: 'Notas', exact: true })).toBeVisible();
  await chip(page, 'Monto').dragTo(chip(page, 'Notas'));
  await expect(section(page, 'Seguimiento').getByRole('button', { name: /^(Monto|Notas)/ })).toHaveText(['Monto', 'Notas']);
  const seguimiento = preview(page).locator('.form-section').filter({ has: page.getByRole('heading', { name: 'Seguimiento' }) });
  await expect(seguimiento.locator('.field-label')).toHaveText(['Monto', 'Notas']);

  // Contacto de vuelta a «Sin colocar»: el formulario lo mostrará en «Otros datos».
  await chip(page, 'Contacto').dragTo(page.getByRole('complementary', { name: /Sin colocar/ }));
  await expect(chip(page, 'Contacto, sin colocar')).toBeVisible();
  await expect(preview(page).getByRole('heading', { name: 'Otros datos' })).toBeVisible();

  // Mover una sección arrastrando su asa: Seguimiento queda antes de Datos de la visita.
  await section(page, 'Seguimiento').locator('.ld-grip').dragTo(section(page, 'Datos de la visita'));
  await expect(preview(page).getByRole('heading', { level: 4 })).toHaveText(['Seguimiento', 'Datos de la visita', 'Otros datos']);

  const plan = await reviewPlan(page);
  expect(plan.operations).toEqual([{ op: 'setLayout', entity: 'Visita', layout: { sections: [
    { name: 'Seguimiento', label: 'Seguimiento', fields: ['Monto', 'Notas'] },
    { name: 'General', label: 'Datos de la visita', fields: ['Cliente', 'Fecha', 'Motivo'] },
  ] } }]);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText("Definir el formulario de 'Visita': 2 sección(es)")).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  expect((await entity('Visita')).layout.sections.map((s: { label: string }) => s.label)).toEqual(['Seguimiento', 'Datos de la visita']);
});

test('H1 CA3 · CA4 · CA5: solo con teclado — tomar, mover, cancelar, quitar, opciones del campo, secciones y pestañas', async ({ page }) => {
  await page.goto('/admin/model?edit=Visita&tab=formulario');
  // Contacto (sin colocar): Espacio lo toma, ↓ lo lleva al inicio de la primera sección y ↓ otra vez una posición más.
  await chip(page, 'Contacto, sin colocar').focus();
  await page.keyboard.press('Space');
  await expect(live(page)).toContainText('Contacto tomado');
  await page.keyboard.press('ArrowDown');
  await expect(live(page)).toContainText('Contacto: sección Seguimiento, posición 1 de 3.');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  // En el borde cruza a la sección siguiente, en el orden en que se lee el formulario.
  await page.keyboard.press('ArrowDown');
  await expect(live(page)).toContainText('Contacto: sección Datos de la visita, posición 1 de 4.');
  await expect(chip(page, 'Contacto')).toBeFocused();
  await page.keyboard.press('Space');
  await expect(live(page)).toContainText('Soltado.');

  // Esc cancela: el campo vuelve a donde estaba al tomarlo.
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowUp');
  await expect(live(page)).toContainText('sección Seguimiento');
  await page.keyboard.press('Escape');
  await expect(live(page)).toContainText('Movimiento cancelado. Contacto: sección Datos de la visita, posición 1 de 4.');

  // Supr lo quita del formulario.
  await page.keyboard.press('Delete');
  await expect(live(page)).toContainText('Contacto quitado del formulario');
  await expect(chip(page, 'Contacto, sin colocar')).toBeVisible();

  // Opciones del campo en un diálogo: sección, ancho completo y condición.
  await page.getByRole('button', { name: 'Opciones de Contacto' }).focus();
  await page.keyboard.press('Enter');
  const dlg = page.getByRole('dialog', { name: 'Campo «Contacto»' });
  await dlg.getByLabel('Sección').selectOption({ label: 'Datos de la visita' });
  await toggle(dlg, /Ancho completo/, true);
  await dlg.getByLabel('Visible solo si').fill("Motivo = 'Soporte'");
  await page.keyboard.press('Escape');
  await expect(dlg).toBeHidden();
  await expect(chip(page, 'Contacto, ancho completo, condicional')).toBeVisible();
  const contacto = preview(page).locator('.preview-field').filter({ hasText: 'Contacto' });
  await expect(contacto).toHaveClass(/span-2/);
  await expect(contacto.getByText('Condicional')).toBeVisible();

  // Propiedades de una sección: columnas y condición.
  await page.getByLabel('Columnas de la sección Seguimiento').selectOption('1');
  await page.getByRole('button', { name: 'Propiedades de la sección Seguimiento' }).click();
  await section(page, 'Seguimiento').getByLabel('Visible solo si').fill('Monto > 0');
  await expect(preview(page).getByText('Visible solo si').first()).toBeVisible();

  // Pestañas: organizar, agregar, renombrar (Enter confirma) y mover una sección a otra pestaña con su botón.
  await toggle(page, 'Organizar en pestañas', true);
  await expect(page.getByRole('group', { name: 'Pestaña General' })).toBeVisible();
  await page.getByRole('button', { name: 'Agregar pestaña' }).click();
  const tabName = page.getByLabel('Nombre de la pestaña 2');
  await tabName.fill('General');
  await expect(page.getByText('Ya hay una pestaña con ese nombre')).toBeVisible();
  await tabName.fill('Cierre');
  await tabName.press('Enter');
  await expect(page.getByRole('group', { name: 'Pestaña Cierre' })).toBeVisible();
  // «Bajar» la última sección de General la pasa a la pestaña Cierre (las pestañas nunca quedan separadas).
  await page.getByRole('button', { name: 'Bajar sección Datos de la visita' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('group', { name: 'Pestaña Cierre' }).getByRole('region', { name: 'Sección Datos de la visita' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Bajar sección Datos de la visita' })).toBeFocused();
  // La sección vacía que creó la pestaña nueva se quita (una sección necesita al menos un campo).
  await page.getByRole('button', { name: 'Quitar sección Nueva sección' }).click();
  const tabs = preview(page).getByRole('tablist', { name: 'Pestañas de la vista previa' });
  await expect(tabs.getByRole('tab')).toHaveText(['General', 'Cierre']);
  await tabs.getByRole('tab', { name: 'Cierre' }).click();
  await expect(preview(page).getByRole('heading', { name: 'Datos de la visita' })).toBeVisible();

  const plan = await reviewPlan(page);
  expect(plan.operations).toEqual([{ op: 'setLayout', entity: 'Visita', layout: { sections: [
    { name: 'Seguimiento', label: 'Seguimiento', tab: 'General', columns: 1, visibleWhen: 'Monto > 0', fields: ['Monto', 'Notas'] },
    { name: 'General', label: 'Datos de la visita', tab: 'Cierre', fields: ['Cliente', 'Fecha', 'Motivo', { field: 'Contacto', wide: true, visibleWhen: "Motivo = 'Soporte'" }] },
  ] } }]);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await expect(dialog.getByText("Definir el formulario de 'Visita': 2 sección(es) en 2 pestaña(s)")).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();

  // El formulario real usa el layout aplicado (Seguimiento, condicionada a Monto > 0, aún no se muestra).
  await page.goto('/c/Visita/new');
  await page.getByRole('tab', { name: 'Cierre' }).click();
  await expect(page.getByLabel('Cliente')).toBeVisible();
});

test('H1 CA6: mover algo y regresarlo no es un cambio', async ({ page }) => {
  await page.goto('/admin/model?edit=Visita&tab=formulario');
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
  await chip(page, 'Cliente').focus();
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Revisar cambios' })).toBeEnabled();
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
  // Activar y desactivar la condición de un campo tampoco deja rastro.
  await page.getByRole('button', { name: 'Opciones de Fecha' }).click();
  const condicion = page.getByRole('dialog').getByLabel('Visible solo si');
  await condicion.fill('Monto > 1');
  await condicion.fill('');
  await page.getByRole('dialog').getByRole('button', { name: 'Listo' }).click();
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();
});

test('H1 CA7 · CA8: JSON alterno sobre el mismo borrador y campos que ya no existen', async ({ page }) => {
  await page.goto('/admin/model?edit=Visita&tab=formulario');
  await page.getByRole('button', { name: 'JSON' }).click();
  const box = page.getByLabel('Layout del formulario (JSON)');
  await expect(box).toHaveValue(/"Datos de la visita"/);
  const valid = await box.inputValue();
  await box.fill(valid.replace('"sections": [', '"sections": [,'));
  await page.getByRole('button', { name: 'Diseñador' }).click();
  await expect(page.getByText('El JSON no es válido')).toBeVisible();
  await page.getByRole('button', { name: 'Corregir el JSON' }).click();
  // Un campo que no existe, escrito a mano: el diseñador lo marca y lo deja quitar.
  await box.fill(valid.replace('"Cliente",', '"Cliente", "Fantasma",'));
  await page.getByRole('button', { name: 'Diseñador' }).click();
  await expect(page.getByText('Fantasma')).toBeVisible();
  await expect(page.getByText('no existe o está archivado')).toBeVisible();
  await page.getByRole('button', { name: 'Quitar Fantasma del formulario' }).click();
  await expect(page.getByText('Fantasma')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Sin cambios' })).toBeDisabled();

  // Archivar un campo en la pestaña Catálogo lo deja marcado en el formulario (el borrador es uno solo).
  await page.getByRole('tab', { name: 'Catálogo' }).click();
  await page.getByRole('button', { name: 'Archivar campo' }).nth(2).click(); // Motivo
  await page.getByRole('tab', { name: 'Formulario' }).click();
  await expect(page.locator('.ld-chip-error')).toContainText('Motivo');
  await page.getByRole('button', { name: 'Quitar Motivo del formulario' }).click();
  const plan = await reviewPlan(page);
  expect(plan.operations.map((o) => o.op)).toEqual(['archiveField', 'setLayout']);
  await page.getByRole('dialog').getByRole('button', { name: 'Volver a editar' }).click();
});

test('H2 CA1 · CA3: el diagrama dibuja estados y transiciones, ordenados y con nombre accesible', async ({ page }) => {
  await page.goto('/admin/model?edit=Tramite&tab=proceso');
  const node = (name: RegExp) => page.getByRole('button', { name });
  await expect(node(/^Estado Borrador, inicial, editable, 1 transición sale$/)).toBeVisible();
  await expect(node(/^Estado Enviado, no editable, 2 transiciones salen$/)).toBeVisible();
  await expect(node(/^Transición Reabrir: de Rechazado a Borrador \(solo automatización\)$/)).toBeVisible();
  // Capas a partir del inicial: Borrador → Enviado → Aprobado/Rechazado, de izquierda a derecha.
  const x = async (n: string) => (await page.locator(`[data-wf-state="${n}"]`).boundingBox())!.x;
  expect(await x('Borrador')).toBeLessThan(await x('Enviado'));
  expect(await x('Enviado')).toBeLessThan(await x('Aprobado'));
  expect(await x('Aprobado')).toBe(await x('Rechazado'));
  // Regresión (revisión exploratoria 019, F-03): una etiqueta larga, centrada en su flecha, tapaba los estados vecinos.
  const overlaps = await page.evaluate(() => {
    const boxes = (sel: string) => [...document.querySelectorAll(sel)].map((n) => n.getBoundingClientRect());
    const nodes = boxes('.wf-node .wf-node-box');
    const labels = boxes('.wf-edge-label > rect');
    const hit = (a: DOMRect, b: DOMRect) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
    return labels.filter((l, i) => nodes.some((n) => hit(l, n)) || labels.some((m, j) => j !== i && hit(l, m))).length;
  });
  expect(overlaps).toBe(0);

  // Teclado: Tab llega a los estados y Enter abre sus propiedades.
  await node(/^Estado Enviado/).focus();
  await page.keyboard.press('Enter');
  const panel = page.getByRole('complementary', { name: 'Propiedades' });
  await expect(panel.getByLabel('Etiqueta')).toHaveValue('Enviado');
  // Un estado guardado conserva su identificador (registros e historial lo usan).
  await expect(panel.getByLabel('Identificador')).toBeDisabled();
  await node(/^Transición Rechazar/).focus();
  await page.keyboard.press('Enter');
  await expect(panel.getByLabel('Etiqueta del botón')).toHaveValue('Rechazar');
  await expect(panel.getByRole('switch', { name: 'Pedir un comentario' })).toBeChecked();
});

test('H2 CA2 · CA5: agregar un estado, conectarlo arrastrando y editar la transición produce el mismo setWorkflow', async ({ page }) => {
  await page.goto('/admin/model?edit=Tramite&tab=proceso');
  await page.getByRole('button', { name: 'Agregar estado' }).click();
  const panel = page.getByRole('complementary', { name: 'Propiedades' });
  await panel.getByLabel('Etiqueta').fill('Archivado');
  await expect(panel.getByLabel('Identificador')).toHaveValue('Archivado');
  await toggle(panel, /Se puede editar/, false);
  await panel.getByLabel('Color').selectOption('warning');

  // Arrastrar de Aprobado a Archivado crea la transición entre ellos (con el diagrama a la vista, como lo haría una persona).
  await page.getByRole('region', { name: /Diagrama del proceso/ }).scrollIntoViewIfNeeded();
  for (const n of ['Aprobado', 'Archivado']) await expectHumanVisible(page.locator(`[data-wf-state="${n}"] rect.wf-node-box`));
  const box = async (n: string) => (await page.locator(`[data-wf-state="${n}"] rect.wf-node-box`).boundingBox())!;
  const a = await box('Aprobado');
  const b = await box('Archivado');
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 8 });
  await page.mouse.up();
  await expect(panel.getByLabel('Etiqueta del botón')).toHaveValue('Pasar a Archivado');
  await panel.getByLabel('Etiqueta del botón').fill('Archivar');
  await expect(panel.getByLabel('Identificador')).toHaveValue('Archivar');
  await panel.getByLabel('Roles de la transición').fill('Administrador');
  await page.keyboard.press('Enter');
  await panel.getByLabel('Condición').fill("Asunto <> ''");
  await panel.getByRole('button', { name: 'Agregar asignación' }).click();
  await panel.getByLabel('Campo de la asignación 1').selectOption('Motivo');
  await panel.getByLabel('Valor de la asignación 1').fill("''");
  await expect(page.getByRole('button', { name: /^Transición Archivar: de Aprobado a Archivado$/ })).toBeVisible();

  const plan = await reviewPlan(page);
  expect(plan.operations).toHaveLength(1);
  expect(plan.operations[0]).toMatchObject({ op: 'setWorkflow', entity: 'Tramite' });
  const wf = (plan.operations[0] as unknown as { workflow: { states: object[]; transitions: object[] } }).workflow;
  expect(wf.states).toContainEqual({ name: 'Archivado', label: 'Archivado', color: 'warning', editable: false });
  expect(wf.transitions).toContainEqual({ name: 'Archivar', label: 'Archivar', from: ['Aprobado'], to: 'Archivado', roles: ['Administrador'], when: "Asunto <> ''", set: [{ field: 'Motivo', value: "''" }] });
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText("Actualizar proceso de 'Trámite': 5 estados, 5 transiciones")).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  expect((await entity('Tramite')).workflow.transitions.map((t: { name: string }) => t.name)).toContain('Archivar');
});

test('H2 CA3 · CA4: eliminar un estado pide confirmación y dice qué transiciones se van; el inicial no se elimina', async ({ page }) => {
  await page.goto('/admin/model?edit=Tramite&tab=proceso');
  await page.getByRole('button', { name: /^Estado Borrador/ }).click();
  const panel = page.getByRole('complementary', { name: 'Propiedades' });
  await expect(panel.getByRole('button', { name: 'Eliminar estado' })).toBeDisabled();
  await page.getByRole('button', { name: /^Estado Rechazado/ }).focus();
  await page.keyboard.press('Enter');
  await panel.getByRole('button', { name: 'Eliminar estado' }).click();
  const confirm = page.getByRole('dialog', { name: 'Eliminar el estado «Rechazado»' });
  await expect(confirm).toContainText('Se quitan las transiciones: Rechazar, Reabrir.');
  // Spec 021 (H8 CA3): el servidor ya no deja registros en un estado que desaparece.
  await expect(confirm).toContainText('si hay registros en este estado, el plan se rechazará');
  await confirm.getByRole('button', { name: 'Eliminar estado' }).click();
  await expect(page.getByRole('button', { name: /^Estado Rechazado/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Transición Rechazar/ })).toHaveCount(0);
  const plan = await reviewPlan(page);
  expect(JSON.stringify(plan)).not.toContain('Rechazado');
  await page.getByRole('dialog').getByRole('button', { name: 'Volver a editar' }).click();
  await page.getByRole('button', { name: 'Cancelar' }).click();
  // Spec 021 (H8 CA4): con cambios sin aplicar, cancelar pide confirmación.
  await page.getByRole('dialog', { name: '¿Descartar los cambios?' }).getByRole('button', { name: 'Descartar cambios' }).click();
  await expect(page).toHaveURL(/\/admin\/model$/);
});

test('H2 CA4: un catálogo sin proceso ofrece crearlo con un estado inicial', async ({ page }) => {
  await page.goto('/admin/model?edit=Visita&tab=proceso');
  await expect(page.getByText('Este catálogo no tiene proceso')).toBeVisible();
  await page.getByRole('button', { name: 'Crear proceso' }).click();
  await expect(page.getByRole('button', { name: /^Estado Borrador, inicial, editable, 0 transiciones salen$/ })).toBeVisible();
  const plan = await reviewPlan(page);
  expect(plan.operations).toEqual([{ op: 'setWorkflow', entity: 'Visita', workflow: {
    initial: 'Borrador', states: [{ name: 'Borrador', label: 'Borrador', color: 'neutral', editable: true }], transitions: [],
  } }]);
  await expect(page.getByRole('dialog').getByText("Definir proceso de 'Visita': 1 estados, 0 transiciones")).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Volver a editar' }).click();
});

test('H3 CA2 · CA4: sin permiso de modelador no se llega por URL; una partida explica dónde se diseña', async ({ page, browser }) => {
  const ctx = await browser.newContext({ storageState: undefined });
  const other = await ctx.newPage();
  await uiLogin(other, READER, PWD);
  await other.goto('/admin/model?edit=Visita&tab=formulario');
  await expect(other.getByText('Sin acceso')).toBeVisible();
  await expect(other.getByRole('tab', { name: 'Formulario' })).toHaveCount(0);
  await ctx.close();

  await page.goto('/admin/model?edit=TramitePartida&tab=formulario');
  await expect(page.getByText('Es un catálogo de partidas')).toBeVisible();
  await page.getByRole('tab', { name: 'Proceso' }).click();
  await expect(page.getByText('Es un catálogo de partidas')).toBeVisible();
});

test('H3 CA3: accesibilidad AA en claro y oscuro, y a 390 px la página no se desborda', async ({ page }) => {
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto('/admin/model?edit=Visita&tab=formulario');
    await expect(page.getByRole('region', { name: 'Vista previa' })).toBeVisible();
    await expectAccessible(page);
    await page.goto('/admin/model?edit=Tramite&tab=proceso');
    await page.getByRole('button', { name: /^Transición Enviar/ }).click();
    await expectAccessible(page);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  for (const tab of ['formulario', 'proceso']) {
    await page.goto(`/admin/model?edit=Tramite&tab=${tab}`);
    await expect(page.getByRole('tab', { name: 'Proceso' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  }
  // El diagrama se desplaza dentro de su recuadro, que se recorre con el teclado.
  const canvas = page.getByRole('region', { name: /Diagrama del proceso/ });
  await canvas.focus();
  await expect(canvas).toBeFocused();
  expect(await canvas.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => canvas.evaluate((el) => el.scrollLeft)).toBeGreaterThan(0);
});
