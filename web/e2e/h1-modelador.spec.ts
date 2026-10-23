// Spec 001 · H1 (crear catálogo) y H2 (agregar campos), recorridos como lo haría una persona.
import { expect, expectAccessible, test } from './helpers';

test.describe.configure({ mode: 'serial' });

test('H1: crear un catálogo desde el modelador y capturar datos sin programar', async ({ page }) => {
  await page.goto('/admin/model');
  await page.getByRole('button', { name: 'Nuevo catálogo' }).first().click();

  // El identificador y el plural se sugieren a partir del nombre.
  await page.getByLabel('Nombre (singular)').fill('Almacén');
  await expect(page.getByLabel(/^Identificador(?! del campo)/)).toHaveValue('Almacen');
  await expect(page.getByLabel('Nombre (plural)')).toHaveValue('Almacenes');

  // Segundo campo: lista de opciones.
  await page.getByRole('button', { name: 'Agregar campo' }).click();
  await page.getByLabel('Etiqueta del campo').last().fill('Tipo');
  await page.getByLabel('Tipo de campo').last().selectOption({ label: 'Lista de opciones' });
  const options = page.getByLabel('Opciones de la lista');
  for (const o of ['Seco', 'Refrigerado']) { await options.fill(o); await options.press('Enter'); }

  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan es válido')).toBeVisible();
  await dialog.getByText('Ver SQL').click();
  await expect(dialog.getByText('CREATE TABLE [app].[Almacen]')).toBeVisible();
  await expectAccessible(page);
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();

  await expect(page.getByText('Cambios aplicados')).toBeVisible();
  const nav = page.getByLabel('Navegación principal');
  await nav.getByRole('link', { name: 'Almacenes' }).click();

  // CA1: el catálogo nuevo ya permite capturar información.
  await page.getByRole('button', { name: 'Nuevo', exact: true }).first().click();
  await page.getByLabel('Nombre').fill('Central');
  await page.getByLabel('Tipo').selectOption('Refrigerado');
  await page.getByRole('button', { name: 'Crear' }).click();
  await expect(page.getByText('Registro creado')).toBeVisible();
  await nav.getByRole('link', { name: 'Almacenes' }).click();
  await expect(page.getByRole('cell', { name: 'Central' })).toBeVisible();
  await expect(page.getByText('Refrigerado')).toBeVisible();
});

test('H1 CA3: identificadores inválidos o reservados se rechazan con mensaje claro', async ({ page }) => {
  await page.goto('/admin/model?new=1');
  await page.getByLabel('Nombre (singular)').fill('Prueba');

  await page.getByLabel(/^Identificador(?! del campo)/).fill('1Malo');
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  await expect(page.getByText(/Solo letras, números y guion bajo/)).toBeVisible();

  // Palabra reservada: pasa la validación local pero el servidor la rechaza en la vista previa.
  await page.getByLabel(/^Identificador(?! del campo)/).fill('Select');
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('El plan tiene errores')).toBeVisible();
  await expect(dialog.getByText(/palabra reservada/)).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Aplicar cambios' })).toBeDisabled();
});

test('H2: agregar un campo a un catálogo con datos conserva la información', async ({ page }) => {
  await page.goto('/admin/model');
  await page.getByRole('row', { name: /Almacenes/ }).getByRole('button', { name: 'Editar' }).click();
  await page.getByRole('button', { name: 'Agregar campo' }).click();
  await page.getByLabel('Etiqueta del campo').last().fill('Capacidad');
  await page.getByLabel('Tipo de campo').last().selectOption({ label: 'Entero' });
  await page.getByRole('button', { name: 'Revisar cambios' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/Agregar campo 'Capacidad'/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Aplicar cambios' }).click();
  await expect(page.getByText('Cambios aplicados')).toBeVisible();

  await page.goto('/c/Almacen');
  await expect(page.getByRole('cell', { name: 'Central' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: /Capacidad/ })).toBeVisible();
});
