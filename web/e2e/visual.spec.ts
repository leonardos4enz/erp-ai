// Regresión visual: compara capturas contra las aprobadas en e2e/__screenshots__.
// Si un cambio de diseño es intencional: npm run e2e -- --update-snapshots, revisa las imágenes y súbelas.
import { expect, test } from './helpers';

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`tema ${scheme}`, () => {
    test.use({ colorScheme: scheme });

    test.beforeEach(async ({ page }) => {
      // Fija el tema elegido para que no dependa de una preferencia guardada.
      await page.addInitScript((s) => { try { localStorage.setItem('erp.theme', s); } catch { /* sin storage */ } }, scheme);
    });

    test('login', async ({ browser }) => {
      const ctx = await browser.newContext({ colorScheme: scheme, storageState: undefined, viewport: { width: 1280, height: 800 } });
      const page = await ctx.newPage();
      await page.goto('/login');
      await expect(page.getByRole('button', { name: 'Entrar' })).toBeVisible();
      await expect(page).toHaveScreenshot(`login-${scheme}.png`);
      await ctx.close();
    });

    test('formulario dinámico', async ({ page }) => {
      await page.goto('/c/Empaque/new');
      await expect(page.getByLabel('Clave')).toBeVisible();
      await expect(page).toHaveScreenshot(`formulario-${scheme}.png`);
    });

    test('modelador', async ({ page }) => {
      await page.goto('/admin/model?new=1');
      await expect(page.getByLabel('Nombre (singular)')).toBeVisible();
      await expect(page).toHaveScreenshot(`modelador-${scheme}.png`, { fullPage: true });
    });
  });
}

test('móvil: el formulario se adapta sin scroll horizontal', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/c/Empaque/new');
  await expect(page.getByLabel('Clave')).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expect(page).toHaveScreenshot('formulario-movil.png');
});

// Regresión (revisión exploratoria 001): en móvil el menú abría encima del formulario y lo tapaba.
test('móvil: el menú arranca cerrado, no tapa el contenido y se cierra al navegar', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/c/Empaque/new');
  const clave = page.getByLabel('Clave');
  const isTopmost = () => clave.evaluate((el) => {
    const r = el.getBoundingClientRect();
    return document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) === el;
  });
  expect(await isTopmost(), 'el campo debe estar visible, no debajo del menú').toBe(true);

  await page.getByRole('banner').getByRole('button', { name: 'Expandir menú' }).click();
  const nav = page.getByLabel('Navegación principal');
  await nav.getByRole('link', { name: 'Camiones' }).click();
  await expect(page).toHaveURL(/\/c\/Camion$/);
  await expect(nav.getByRole('link', { name: 'Camiones' })).not.toBeInViewport();

  await page.getByRole('banner').getByRole('button', { name: 'Expandir menú' }).click();
  await page.keyboard.press('Escape');
  await expect(nav.getByRole('link', { name: 'Camiones' })).not.toBeInViewport();
});

// Regresión (reporte del usuario tras la spec 010): con el menú contraído, los títulos de grupo ocultos conservaban su ancho
// y la barra lateral mostraba desplazamiento horizontal.
test('el menú contraído no se desplaza horizontalmente', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('banner').getByRole('button', { name: 'Contraer menú' }).click();
  const nav = page.locator('.sidebar-nav');
  await expect(page.getByLabel('Navegación principal')).toHaveClass(/collapsed/);
  // La barra se contrae con una transición: se mide con el ancho final.
  await expect.poll(() => nav.evaluate((n) => n.getBoundingClientRect().width)).toBeLessThan(80);
  await page.evaluate(() => Promise.all(document.getAnimations().map((a) => a.finished.catch(() => undefined))));
  expect(await nav.evaluate((n) => n.scrollWidth - n.clientWidth)).toBeLessThanOrEqual(0);
  await page.getByRole('banner').getByRole('button', { name: 'Expandir menú' }).click();
});
