// Spec 053 · matriz de autorización: el administrador da de alta perfiles y una regla en la pantalla (H2 CA1), el panel del
// registro muestra el avance y quien le toca autoriza o rechaza con motivo (H3 CA1–CA3), y la persona solo ve el documento
// cuando le toca (H4 CA4). Autocontenido: crea su catálogo (grupo «Pruebas 053»), sus personas y su matriz; archiva al terminar.
import type { Page } from '@playwright/test';
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, expectToast, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const GROUP = 'Pruebas 053';
const E = 'AeSolicitud';
const ROLES = ['E2E Az Solicitante', 'E2E Az Autorizador'];
const PWD = 'frase larga de autorizaciones e2e 053';
const PEOPLE = { sol: 'e2e53-sol', jefa: 'e2e53-jefa', gerente: 'e2e53-gerente' } as const;
const NAMES = { sol: 'Solicitante 053', jefa: 'Jefa Ana 053', gerente: 'Gerente Beto 053' } as const;

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  if ((await api.get(`/api/model/entities/${E}`)).ok()) return;
  const roles = (await (await api.get('/api/admin/roles')).json()) as { name: string }[];
  for (const name of ROLES.filter((n) => !roles.some((r) => r.name === n))) {
    const r = await api.post('/api/admin/roles', { data: { name, description: 'E2E spec 053', canManageModel: false, canManageUsers: false, canViewAudit: false, permissions: [] } });
    expect(r.status(), await r.text()).toBe(200);
  }
  await applyPlan(api, {
    description: 'E2E 053: matriz de autorización',
    operations: [
      { op: 'createEntity', group: GROUP, name: 'AeDepto', label: 'Depto 053', labelPlural: 'Deptos 053', displayField: 'Nombre',
        fields: [{ name: 'Nombre', label: 'Nombre', type: 'text', required: true }] },
      { op: 'createEntity', group: GROUP, name: E, label: 'Solicitud 053', labelPlural: 'Solicitudes 053', displayField: 'Folio', perCompany: true,
        fields: [
          { name: 'Folio', label: 'Folio', type: 'autonumber', prefix: 'AE-', padding: 4 },
          { name: 'Concepto', label: 'Concepto', type: 'text' },
          { name: 'Departamento', label: 'Departamento', type: 'reference', referenceEntity: 'AeDepto', required: true },
          { name: 'Monto', label: 'Monto', type: 'decimal', precision: 18, scale: 2 },
          { name: 'Autorizadores', label: 'Autorizadores', type: 'multiuser', editable: false, showInList: false },
        ] },
      { op: 'setWorkflow', entity: E, workflow: {
        initial: 'Borrador',
        states: [
          { name: 'Borrador', label: 'Borrador', editable: true }, { name: 'PorAutorizar', label: 'Por autorizar', color: 'warning', editable: false },
          { name: 'Autorizada', label: 'Autorizada', color: 'success', editable: false }, { name: 'Rechazada', label: 'Rechazada', color: 'danger', editable: false },
        ],
        transitions: [
          { name: 'enviar', label: 'Enviar a autorizar', from: ['Borrador'], to: 'PorAutorizar', variant: 'primary' },
          { name: 'autorizada', label: 'Autorizada', from: ['PorAutorizar'], to: 'Autorizada', system: true },
          { name: 'rechazada', label: 'Rechazada', from: ['PorAutorizar'], to: 'Rechazada', system: true },
        ],
        approval: { state: 'PorAutorizar', approve: 'autorizada', reject: 'rechazada', amount: 'Monto', conditions: ['Departamento'], approvers: 'Autorizadores' },
      } },
      { op: 'setSecurity', entity: E, security: { records: [
        { roles: [ROLES[0]], scope: 'own' },
        { roles: [ROLES[1]], where: 'CURRENTUSER() IN Autorizadores', access: 'read' },
      ] } },
    ],
  });
  const perms = [
    { entity: E, canRead: true, canCreate: true, canUpdate: true, canDelete: false },
    { entity: 'AeDepto', canRead: true, canCreate: false, canUpdate: false, canDelete: false },
  ];
  for (const name of ROLES)
    expect((await api.put(`/api/admin/roles/${await roleId(api, name)}`, { data: { name, description: 'E2E spec 053', canManageModel: false,
      canManageUsers: false, canViewAudit: false, permissions: perms } })).status()).toBe(200);
  const users = (await (await api.get('/api/admin/users')).json()) as { username: string }[];
  for (const [key, username] of Object.entries(PEOPLE).filter(([, u]) => !users.some((x) => x.username === u))) {
    const role = await roleId(api, key === 'sol' ? ROLES[0] : ROLES[1]);
    const u = await api.post('/api/admin/users', { data: { username, displayName: NAMES[key as keyof typeof NAMES], password: PWD, roleIds: [role] } });
    expect(u.status(), await u.text()).toBe(201);
  }
  expect((await api.post('/api/data/AeDepto', { data: { Nombre: 'Compras 053' } })).status()).toBe(201);
});

test.afterAll(async () => {
  if (process.env.E2E_INDEPENDENT) return;
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const model = (await (await api.get('/api/model/entities')).json()) as { name: string }[];
  const operations = [E, 'AeDepto'].filter((n) => model.some((e) => e.name === n)).map((entity) => ({ op: 'archiveEntity', entity }));
  if (operations.length) await applyPlan(api, { description: 'E2E 053: limpieza', operations });
});

/** Agrega una persona desde el buscador, como una persona: la opción se ve completa (lección de `pick()`, F-20). */
async function addPerson(page: Page, field: string, search: string, option: string) {
  const box = page.getByRole('combobox', { name: field });
  await box.fill(search);
  const first = page.getByRole('listbox').getByRole('option').first();
  await expect(first).toHaveAccessibleName(option);
  const visibleToHuman = await first.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return r.bottom <= window.innerHeight && (hit === el || el.contains(hit));
  });
  expect(visibleToHuman, `la opción ${option} debe verse completa`).toBe(true);
  await box.press('Enter');
}

async function profile(page: Page, name: string, person: keyof typeof NAMES) {
  await page.getByRole('button', { name: 'Nuevo perfil' }).click();
  const dialog = page.getByRole('dialog', { name: 'Nuevo perfil' });
  await dialog.getByLabel('Nombre').fill(name);
  await addPerson(page, 'Personas', NAMES[person].split(' ')[0], NAMES[person]);
  await expectToast(page, 'Perfil creado', () => dialog.getByRole('button', { name: 'Guardar' }).click());
  await expect(page.getByRole('row', { name: new RegExp(name) })).toContainText(NAMES[person]);
}

test('H2 CA1: el administrador da de alta dos perfiles y una regla de dos niveles en la pantalla', async ({ page }) => {
  await page.goto('/admin/approvals');
  await expect(page.getByRole('heading', { name: 'Autorizaciones' })).toBeVisible();
  await profile(page, 'Jefes 053', 'jefa');
  await profile(page, 'Gerentes 053', 'gerente');
  await expectAccessible(page);

  await page.getByRole('tab', { name: 'Reglas' }).click();
  await page.getByRole('button', { name: 'Nueva regla' }).click();
  const dialog = page.getByRole('dialog', { name: 'Nueva regla' });
  await dialog.getByLabel('Catálogo').selectOption({ label: 'Solicitudes 053' });
  await dialog.getByLabel('Nombre').fill('General 053');
  await dialog.getByLabel('Perfil del nivel 1').selectOption({ label: 'Nivel 1: Jefes 053' });
  await dialog.getByRole('button', { name: 'Agregar nivel' }).click();
  await dialog.getByLabel('Perfil del nivel 2').selectOption({ label: 'Nivel 2: Gerentes 053' });
  await expectAccessible(page);
  await expectToast(page, 'Regla creada', () => dialog.getByRole('button', { name: 'Guardar' }).click());
  await expect(page.getByRole('row', { name: /General 053/ })).toContainText('Jefes 053 → Gerentes 053');

  const api = await apiAs(ADMIN.username, ADMIN.password);
  const config = await (await api.get('/api/admin/approvals')).json();
  const rule = config.rules.find((r: { name: string }) => r.name === 'General 053');
  const ids = ['Jefes 053', 'Gerentes 053'].map((n) => config.profiles.find((p: { name: string }) => p.name === n).id);
  expect(rule.levels).toEqual(ids);
});

async function sent(): Promise<{ id: number; folio: string }> {
  const sol = await apiAs(PEOPLE.sol, PWD);
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const depto = (await (await admin.get('/api/data/AeDepto', { params: { search: 'Compras 053' } })).json()).items[0].id;
  const row = await (await sol.post(`/api/data/${E}`, { data: { Concepto: 'Sillas para la sala', Departamento: depto, Monto: 12500 } })).json();
  const res = await sol.post(`/api/data/${E}/${row.id}/transition`, { data: { transition: 'enviar', _rowVersion: row._rowVersion } });
  expect(res.status(), await res.text()).toBe(200);
  return { id: row.id, folio: row.Folio };
}

test('H3 CA1–CA3 · H4 CA4: el panel muestra el avance, quien le toca autoriza con teclado y rechazar pide motivo', async ({ browser }) => {
  const { id, folio } = await sent();
  const as = async (who: keyof typeof PEOPLE) => {
    const ctx = await browser.newContext({ storageState: undefined });
    const page = await ctx.newPage();
    await uiLogin(page, PEOPLE[who], PWD);
    return { ctx, page };
  };

  // H4 CA4: al gerente todavía no le toca: no lo ve en la lista.
  const gerente = await as('gerente');
  await gerente.page.goto(`/c/${E}`);
  await expect(gerente.page.getByRole('heading', { name: 'Solicitudes 053' })).toBeVisible();
  await expect(gerente.page.getByRole('row').filter({ hasText: folio })).toBeHidden();

  const jefa = await as('jefa');
  try {
    await jefa.page.goto(`/c/${E}`);
    await expect(jefa.page.getByRole('row').filter({ hasText: folio })).toBeVisible();
    await jefa.page.goto(`/c/${E}/${id}`);
    const panel = jefa.page.getByRole('region', { name: 'Autorización' });
    await expect(panel.getByText('Regla «General 053»')).toBeVisible();
    const levels = panel.getByRole('list', { name: 'Niveles de autorización' }).getByRole('listitem');
    await expect(levels.nth(0)).toContainText('Nivel 1 · Jefes 053');
    await expect(levels.nth(0)).toContainText('En curso');
    await expect(levels.nth(0)).toContainText(`Pueden autorizar: ${NAMES.jefa}`);
    await expect(levels.nth(1)).toContainText('Pendiente');
    await expectAccessible(jefa.page);
    // Con teclado: el botón recibe el foco y se activa con Enter; el diálogo enfoca el comentario.
    await panel.getByRole('button', { name: 'Autorizar' }).focus();
    await jefa.page.keyboard.press('Enter');
    const dialog = jefa.page.getByRole('dialog', { name: 'Autorizar' });
    await expect(dialog.getByLabel('Comentario')).toBeFocused();
    await jefa.page.keyboard.type('Presupuesto disponible');
    // Ya no le toca: deja de verlo (H4 CA4) y vuelve a su lista, donde el documento ya no está.
    await expectToast(jefa.page, 'Nivel autorizado', () => dialog.getByRole('button', { name: 'Autorizar' }).click());
    await expect(jefa.page).toHaveURL(new RegExp(`/c/${E}$`));
    await expect(jefa.page.getByRole('heading', { name: 'Solicitudes 053' })).toBeVisible();
    await expect(jefa.page.getByRole('row').filter({ hasText: folio })).toBeHidden();
  } finally {
    await jefa.ctx.close();
  }

  try {
    // Ahora sí le toca al gerente: lo ve en su lista (su bandeja) y lo rechaza con motivo.
    await gerente.page.reload();
    await expect(gerente.page.getByRole('row').filter({ hasText: folio })).toBeVisible();
    await gerente.page.goto(`/c/${E}/${id}`);
    const panel = gerente.page.getByRole('region', { name: 'Autorización' });
    await expect(panel.getByRole('listitem').nth(0)).toContainText(`Autorizó ${NAMES.jefa}`);
    await expect(panel.getByRole('listitem').nth(0)).toContainText('Presupuesto disponible');
    await expect(panel.getByRole('listitem').nth(1)).toContainText('En curso');
    await panel.getByRole('button', { name: 'Rechazar' }).click();
    const dialog = gerente.page.getByRole('dialog', { name: 'Rechazar' });
    await dialog.getByRole('button', { name: 'Rechazar' }).click();
    await expect(dialog.getByText('Escribe el motivo del rechazo')).toBeVisible();
    await dialog.getByLabel('Motivo del rechazo').fill('Ya hay sillas en el almacén');
    await expectToast(gerente.page, 'Documento rechazado', () => dialog.getByRole('button', { name: 'Rechazar' }).click());
    await expect(gerente.page).toHaveURL(new RegExp(`/c/${E}$`));
  } finally {
    await gerente.ctx.close();
  }
  const admin = await apiAs(ADMIN.username, ADMIN.password);
  const done = await (await admin.get(`/api/data/${E}/${id}`)).json();
  expect(done.Estado).toBe('Rechazada');
  expect(done._approval.levels[1].decisions[0].comment).toBe('Ya hay sillas en el almacén');
});

// Capturas: la matriz (datos propios del archivo), en claro, oscuro y móvil.
for (const [name, scheme, viewport] of [['light', 'light', { width: 1280, height: 900 }], ['dark', 'dark', { width: 1280, height: 900 }],
  ['movil', 'light', { width: 390, height: 844 }]] as const) {
  test(`visual: matriz de autorización en ${name === 'movil' ? 'móvil' : name === 'light' ? 'claro' : 'oscuro'}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.setViewportSize(viewport);
    await page.goto('/admin/approvals');
    await page.getByRole('tab', { name: 'Reglas' }).click();
    await expect(page.getByRole('row', { name: /General 053/ })).toBeVisible();
    await page.mouse.move(0, 0);
    await page.evaluate(() => document.fonts.ready);
    await expect(page.getByRole('main')).toHaveScreenshot(`matriz-${name}.png`);
    if (name === 'movil') {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    }
  });
}
