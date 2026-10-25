// Spec 001 · H5 (usuarios, roles, permisos) y H6 (auditoría).
import { ADMIN, apiAs, applyPlan, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });

const capturista = { username: 'ui-capturista', password: 'frase larga para la interfaz 2026' };

test.beforeAll(async () => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  await applyPlan(api, {
    description: 'Catálogo de clientes (UI)',
    operations: [{ op: 'createEntity', name: 'Cliente', label: 'Cliente', labelPlural: 'Clientes', icon: 'users',
      fields: [{ name: 'RazonSocial', label: 'Razón social', type: 'text', required: true }] }],
  });
  await api.post('/api/data/Cliente', { data: { RazonSocial: 'Cartones del Norte' } });
});

test('H5: crear un usuario desde la UI', async ({ page }) => {
  await page.goto('/admin/security');
  await page.getByRole('button', { name: 'Nuevo usuario' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('textbox', { name: /^Usuario/ }).fill(capturista.username);
  await dialog.getByRole('textbox', { name: /^Nombre/ }).fill('Capturista UI');
  await dialog.getByLabel(/^Contraseña/).fill(capturista.password);
  await dialog.getByRole('checkbox', { name: /^Usuario ·/ }).check(); // rol "Usuario"
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Crear usuario' }).click();
  await expect(page.getByRole('cell', { name: /ui-capturista/ })).toBeVisible();
});

test('H5: sin permisos el catálogo no aparece y el acceso directo muestra "sin permiso"', async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, capturista.username, capturista.password);
  const nav = page.getByLabel('Navegación principal');
  await expect(nav.getByRole('link', { name: 'Clientes' })).toBeHidden();
  await expect(nav.getByRole('link', { name: 'Modelador' })).toBeHidden();
  await page.goto('/c/Cliente');
  await expect(page.getByText(/permiso/i).first()).toBeVisible();
  await ctx.close();
});

test('H5: al dar permiso de lectura el usuario ve el catálogo, pero no puede crear', async ({ browser }) => {
  const api = await apiAs(ADMIN.username, ADMIN.password);
  const id = await roleId(api, 'Usuario');
  const res = await api.put(`/api/admin/roles/${id}`, { data: {
    name: 'Usuario', description: 'Captura de información según permisos por catálogo.',
    canManageModel: false, canManageUsers: false, canViewAudit: false,
    permissions: [{ entity: 'Cliente', canRead: true, canCreate: false, canUpdate: false, canDelete: false }],
  } });
  expect(res.status()).toBe(200);

  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, capturista.username, capturista.password);
  await page.getByLabel('Navegación principal').getByRole('link', { name: 'Clientes' }).click();
  await expect(page.getByRole('cell', { name: 'Cartones del Norte' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Nuevo', exact: true })).toBeHidden();
  await ctx.close();
});

test('H6: la auditoría muestra quién cambió qué', async ({ page }) => {
  await page.goto('/admin/audit');
  // Spec 024: antes la primera coincidencia era el enlace «Clientes» del menú plano; ahora se busca en la tabla.
  await expect(page.getByRole('cell', { name: 'Cliente', exact: true }).first()).toBeVisible();
  await expect(page.getByText(/model\.change|Cambio de modelo|modelo/i).first()).toBeVisible();
  await expectAccessible(page);
});

test('Sesión: cerrar sesión regresa al login y protege las rutas', async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: undefined });
  const page = await ctx.newPage();
  await uiLogin(page, capturista.username, capturista.password);
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: /Cerrar sesión/ }).click();
  await expect(page).toHaveURL(/\/login/);
  await page.goto('/c/Cliente');
  await expect(page).toHaveURL(/\/login/);
  await ctx.close();
});
