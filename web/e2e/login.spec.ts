// Spec 032: rediseño del inicio de sesión. Un escenario por criterio (H1–H4) y la CSP. La página es anónima con el
// *fixture* `page`, así corren las guardias de la 030 (alerta y foco tras el 401, desborde). Independiente: prepara sus
// propios usuarios (uno se bloquea a propósito) y no toca la cuenta de admin con contraseñas incorrectas.
import type { APIRequestContext, Page } from '@playwright/test';
import { ADMIN, anonApi, apiAs, expect, expectAccessible, roleId, test, uiLogin } from './helpers';

test.describe.configure({ mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial' });
test.use({ storageState: { cookies: [], origins: [] } });

const PWD = 'frase larga del login 032';
const BLOQUEADO = 'e2e-login-bloqueado';
const FALLIDO = 'e2e-login-fallido';
const OTRO = 'e2e-login-otro';
const CSP = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
  + "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

let adminApi: APIRequestContext | undefined;
const admin = async () => (adminApi ??= await apiAs(ADMIN.username, ADMIN.password));

const usuario = (page: Page) => page.getByLabel('Usuario');
const contrasena = (page: Page) => page.getByLabel('Contraseña', { exact: true });
const entrar = (page: Page) => page.getByRole('button', { name: /^(Entrar|Entrando…)$/ });

async function intentar(page: Page, username: string, password: string) {
  await usuario(page).fill(username);
  await contrasena(page).fill(password);
  await entrar(page).click();
}

/** Propiedades que anima cada animación de la página (las de cada fotograma, sin las de control). */
const propiedadesAnimadas = (page: Page) => page.evaluate(() => document.getAnimations().map((a) => ({
  tipo: a.constructor.name,
  props: [...new Set(((a.effect as KeyframeEffect | null)?.getKeyframes() ?? []).flatMap((k) => Object.keys(k)))]
    .filter((p) => !['offset', 'computedOffset', 'easing', 'composite'].includes(p)),
})));

test('preparar: una cuenta que se bloqueará y otra para la contraseña incorrecta', async () => {
  const a = await admin();
  const existentes = ((await (await a.get('/api/admin/users')).json()) as { username: string }[]).map((u) => u.username);
  for (const username of [BLOQUEADO, FALLIDO, OTRO]) {
    if (existentes.includes(username)) continue;
    const res = await a.post('/api/admin/users', { data: { username, displayName: username, password: PWD, roleIds: [await roleId(a, 'Usuario')] } });
    expect(res.status(), await res.text()).toBe(201);
  }
  // Cinco intentos fallidos bloquean la cuenta 15 minutos (001); después ni la contraseña correcta entra.
  const anon = await anonApi();
  for (let i = 0; i < 5; i++) expect((await anon.post('/api/auth/login', { data: { username: BLOQUEADO, password: 'incorrecta 032' } })).status()).toBe(401);
  await anon.dispose();
});

// ---------------- H1: composición, marca y accesibilidad ----------------

for (const scheme of ['light', 'dark'] as const) {
  test(`H1 CA1 y CA3: en escritorio, panel de marca con ilustración decorativa y formulario (${scheme}); axe sin violaciones`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto('/login');
    await expect(page.locator('html')).toHaveAttribute('data-theme', scheme);
    const aside = page.getByRole('complementary', { name: 'Acerca de ERP AI' });
    await expect(aside).toBeVisible();
    await expect(aside.getByText('ERP AI', { exact: true })).toBeVisible();
    await expect(aside.getByText('Tu ERP, declarado como datos.')).toBeVisible();
    const ilustracion = page.locator('svg.login-illustration');
    await expect(ilustracion).toBeVisible();
    await expect(ilustracion).toHaveAttribute('aria-hidden', 'true');
    expect(await ilustracion.locator('text').count(), 'sin texto dentro de la imagen').toBe(0);
    await expect(page.getByRole('heading', { level: 1, name: 'Inicia sesión' })).toBeVisible();
    await expect(page.getByRole('button', { name: scheme === 'dark' ? 'Usar tema claro' : 'Usar tema oscuro' })).toBeVisible();
    await expectAccessible(page);
  });
}

for (const [ancho, alto] of [[390, 844], [768, 1024]] as const) {
  test(`H1 CA2: en ${ancho} px no hay ilustración, el logotipo está sobre el formulario y no hay desplazamiento horizontal`, async ({ page }) => {
    await page.setViewportSize({ width: ancho, height: alto });
    await page.goto('/login');
    await expect(usuario(page)).toBeVisible();
    await expect(page.locator('svg.login-illustration')).toBeHidden();
    await expect(page.locator('.login-compact-logo').getByText('ERP AI')).toBeVisible();
    // Sin panel visible, la ilustración no anima (nada corre fuera de la vista); las transiciones del foco no cuentan.
    expect(await page.evaluate(() => document.getAnimations().filter((a) => a.constructor.name === 'CSSAnimation').length)).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    await expectAccessible(page);
  });
}

// ---------------- H2: animación responsable ----------------

test('H2 CA1: con movimiento reducido la página no tiene ninguna animación (ni transiciones)', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/login');
  await expect(page.locator('svg.login-illustration')).toBeVisible();
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  // Tampoco al aparecer un aviso.
  await entrar(page).click();
  await expect(page.getByText('Escribe tu usuario')).toBeVisible();
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  // Ni mientras espera al servidor (revisión adversarial F-05: el indicador del botón giraba).
  let soltar!: () => void;
  const compuerta = new Promise<void>((r) => { soltar = r; });
  await page.route('**/api/auth/login', async (route) => { await compuerta; await route.continue(); });
  await intentar(page, FALLIDO, 'una contraseña equivocada');
  await expect(page.getByRole('button', { name: 'Entrando…' })).toBeVisible();
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  soltar();
  await expect(page.getByRole('alert').filter({ hasText: 'Usuario o contraseña incorrectos' })).toBeVisible();
});

test('H2 CA2: sin esa preferencia, las animaciones son CSS y solo animan transform u opacity', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.goto('/login');
  await expect(page.locator('svg.login-illustration')).toBeVisible();
  const animaciones = await propiedadesAnimadas(page);
  // Todas declaradas en la hoja de estilos (ninguna con JavaScript). Las transiciones son las del foco de los campos.
  expect(animaciones.filter((a) => a.tipo !== 'CSSAnimation' && a.tipo !== 'CSSTransition')).toEqual([]);
  const ambientales = animaciones.filter((a) => a.tipo === 'CSSAnimation');
  // Halo, órbita, cuatro pulsos y tres filas (un minificador que las pierda en silencio también falla aquí).
  const nombres = await page.evaluate(() => document.getAnimations().map((a) => (a as CSSAnimation).animationName).filter(Boolean).sort());
  expect(nombres).toEqual(['ill-breathe', 'ill-flow-1', 'ill-flow-2', 'ill-flow-3', 'ill-flow-4', 'ill-row-in', 'ill-row-in', 'ill-row-in', 'ill-spin']);
  for (const a of ambientales) expect(a.props.filter((p) => p !== 'transform' && p !== 'opacity')).toEqual([]);
});

test('H2 CA3: el formulario se usa con la animación en curso', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.goto('/login');
  await expect(usuario(page)).toBeFocused();
  expect(await page.evaluate(() => document.getAnimations().some((a) => a.playState === 'running'))).toBe(true);
  await page.keyboard.type('alguien');
  await page.keyboard.press('Tab');
  await page.keyboard.type('una frase');
  await expect(usuario(page)).toHaveValue('alguien');
  await expect(contrasena(page)).toHaveValue('una frase');
});

// ---------------- H3: formulario ----------------

test('H3 CA1 y CA4: mostrar la contraseña, ocultarla al enviar y un solo envío mientras espera', async ({ page }) => {
  await page.goto('/login');
  await usuario(page).fill(FALLIDO);
  await contrasena(page).fill('una contraseña equivocada');
  const mostrar = page.getByRole('button', { name: 'Mostrar contraseña' });
  await expect(mostrar).toHaveAttribute('aria-pressed', 'false');
  await mostrar.click();
  await expect(contrasena(page)).toHaveAttribute('type', 'text');
  await expect(page.getByRole('button', { name: 'Ocultar contraseña' })).toHaveAttribute('aria-pressed', 'true');

  // Se retiene la respuesta para ver la espera.
  let soltar!: () => void;
  const compuerta = new Promise<void>((r) => { soltar = r; });
  let envios = 0;
  await page.route('**/api/auth/login', async (route) => { envios++; await compuerta; await route.continue(); });
  // F-04: el tipo se mira al terminar el evento `submit` (en `window`, después del manejador de React), no en el render
  // siguiente: ahí el navegador y el gestor de contraseñas ya vieron el formulario.
  await page.evaluate(() => addEventListener('submit', () => {
    (window as unknown as { __tipo?: string }).__tipo = (document.getElementById('login-password') as HTMLInputElement).type;
  }));
  await entrar(page).click();
  expect(await page.evaluate(() => (window as unknown as { __tipo?: string }).__tipo)).toBe('password');
  const esperando = page.getByRole('button', { name: 'Entrando…' });
  await expect(esperando).toBeVisible();
  await expect(esperando).toHaveAttribute('aria-busy', 'true');
  await expect(esperando).toBeFocused();
  await expect(contrasena(page), 'al enviar vuelve a ocultarse').toHaveAttribute('type', 'password');
  await page.keyboard.press('Enter');
  // `aria-disabled` hace que Playwright espere; una persona sí puede volver a hacer clic.
  await esperando.click({ force: true });
  soltar();
  await expect(page.getByRole('alert').filter({ hasText: 'Usuario o contraseña incorrectos' })).toBeVisible();
  expect(envios, 'un solo envío aunque se insista mientras espera').toBe(1);
  await expect(entrar(page)).toHaveText('Entrar');
});

test('H3 CA2: el aviso de Bloq Mayús aparece en la contraseña y se va al salir del campo', async ({ page }) => {
  await page.goto('/login');
  await contrasena(page).focus();
  // Playwright no cambia el estado de Bloq Mayús: se envía la tecla con el modificador, como lo haría el sistema.
  await contrasena(page).evaluate((el) => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'A', bubbles: true, modifierCapsLock: true } as KeyboardEventInit)));
  const aviso = page.getByRole('status').filter({ hasText: 'Bloq Mayús está activado' });
  await expect(aviso).toBeVisible();
  expect((await contrasena(page).getAttribute('aria-describedby'))?.split(' ')).toContain(await aviso.getAttribute('id'));
  await usuario(page).focus();
  await expect(aviso).toHaveCount(0);
});

test('H3 CA3: etiquetas reales, autocompletado y campos vacíos sin petición', async ({ page }) => {
  await page.goto('/login');
  await expect(usuario(page)).toHaveAttribute('autocomplete', 'username');
  await expect(contrasena(page)).toHaveAttribute('autocomplete', 'current-password');
  await expect(contrasena(page)).toHaveAttribute('type', 'password');
  let envios = 0;
  page.on('request', (r) => { if (r.url().endsWith('/api/auth/login')) envios++; });
  await entrar(page).click();
  await expect(page.getByText('Escribe tu usuario')).toBeVisible();
  await expect(usuario(page)).toBeFocused();
  await expect(usuario(page)).toHaveAttribute('aria-invalid', 'true');
  await usuario(page).fill('   ');
  await page.keyboard.press('Enter');
  await expect(usuario(page), 'solo espacios cuenta como vacío').toBeFocused();
  await usuario(page).fill('alguien');
  await page.keyboard.press('Enter');
  await expect(page.getByText('Escribe tu contraseña')).toBeVisible();
  await expect(contrasena(page)).toBeFocused();
  expect(envios).toBe(0);
});

// ---------------- H4: estados ----------------

test('H4 CA1: usuario inexistente, contraseña incorrecta y cuenta bloqueada dan el mismo aviso; el foco vuelve a la contraseña', async ({ page }) => {
  // Revisión adversarial F-07: la cuenta de verdad está bloqueada (ni la contraseña correcta entra), no solo «no existe».
  const anon = await anonApi();
  expect((await anon.post('/api/auth/login', { data: { username: BLOQUEADO, password: PWD } })).status()).toBe(401);
  const otro = await anonApi();
  expect((await otro.post('/api/auth/login', { data: { username: OTRO, password: PWD } })).status(), 'una cuenta sana sí entra').toBe(200);
  await Promise.all([anon.dispose(), otro.dispose()]);
  await page.goto('/login');
  const textos: string[] = [];
  for (const [username, password] of [['e2e-no-existe-032', PWD], [FALLIDO, 'una contraseña equivocada'], [BLOQUEADO, PWD]]) {
    await intentar(page, username, password);
    const aviso = page.getByRole('alert').filter({ hasText: 'Usuario o contraseña incorrectos' });
    await expect(aviso).toBeVisible();
    await expect(contrasena(page)).toBeFocused();
    await expect(contrasena(page)).toHaveValue('');
    textos.push((await aviso.innerText()).trim());
  }
  expect(new Set(textos).size, `el mismo aviso: ${textos.join(' | ')}`).toBe(1);
  expect(textos[0]).toContain('se bloquea por unos minutos');
  expect(textos[0], 'sin cifras del bloqueo').not.toMatch(/\d/);
  await expect(page).toHaveURL(/\/login$/);
});

test('H4 CA2: el límite de intentos (429) se dice sin cifras ni usuario', async ({ page }) => {
  await page.goto('/login');
  // El 429 real del servidor está en integración (ApiSecurityTests); aquí, la pantalla.
  await page.route('**/api/auth/login', (route) => route.fulfill({ status: 429, body: '' }));
  await intentar(page, FALLIDO, PWD);
  const aviso = page.getByRole('alert').filter({ hasText: 'Demasiados intentos' });
  await expect(aviso).toBeVisible();
  await expect(aviso).toContainText('Espera un momento');
  expect(await aviso.innerText()).not.toMatch(/\d|e2e-login/);
  await expect(contrasena(page)).toBeFocused();
});

test('H4 CA3: sin respuesta o con un 5xx, un aviso para la persona y nunca el texto del servidor', async ({ page }) => {
  await page.goto('/login');
  await page.route('**/api/auth/login', (route) => route.abort('connectionrefused'));
  await intentar(page, FALLIDO, PWD);
  await expect(page.getByRole('alert').filter({ hasText: 'No pudimos conectar con el servidor' })).toBeVisible();
  await expect(contrasena(page)).toBeFocused();

  await page.unroute('**/api/auth/login');
  await page.route('**/api/auth/login', (route) => route.fulfill({
    status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'SqlException: detalle interno', detail: 'Server=db01' }),
  }));
  await intentar(page, FALLIDO, PWD);
  const aviso = page.getByRole('alert').filter({ hasText: 'No pudimos conectar con el servidor' });
  await expect(aviso).toBeVisible();
  await expect(page.getByText(/SqlException|db01/)).toHaveCount(0);
  // El formulario sigue usable y sin sesión a medias.
  expect((await page.context().cookies()).some((c) => c.name === 'erp.auth')).toBe(false);
});

test('H4 CA4: una sesión que expira lo dice y regresa a donde estaba; cerrar sesión también lo dice; nada queda guardado', async ({ page }) => {
  await uiLogin(page, ADMIN.username, ADMIN.password);
  // La cookie desaparece (expiró o se revocó) con la SPA abierta; la siguiente petición responde 401.
  await page.context().clearCookies();
  await page.evaluate(() => { history.pushState({}, '', '/admin/audit'); dispatchEvent(new PopStateEvent('popstate')); });
  await expect(page).toHaveURL(/\/login$/);
  const expiro = page.getByRole('status').filter({ hasText: 'Tu sesión expiró' });
  await expect(expiro).toBeVisible();
  // F-06: el aviso que ya estaba al cargar se lee con el campo que tiene el foco.
  await expect(usuario(page)).toBeFocused();
  expect((await usuario(page).getAttribute('aria-describedby'))?.split(' ')).toContain('login-notice');
  await intentar(page, ADMIN.username, ADMIN.password);
  await expect(page).toHaveURL(/\/admin\/audit$/);

  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: /Cerrar sesión/ }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('status').filter({ hasText: 'Cerraste sesión' })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Tu sesión expiró' })).toHaveCount(0);

  // Sin credenciales ni motivo en el almacenamiento del navegador (solo en memoria): recargar lo olvida.
  const guardado = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
  expect(guardado).not.toContain(ADMIN.password);
  expect(guardado).not.toMatch(/expired|signedOut|Cerraste/);
  await page.reload();
  await expect(usuario(page)).toBeVisible();
  await expect(page.getByText('Cerraste sesión')).toHaveCount(0);
});

test('la CSP del login no cambia (sin unsafe-inline, fuentes ni CDN)', async ({ page }) => {
  const res = await page.goto('/login');
  expect(res?.headers()['content-security-policy']).toBe(CSP);
  // Las violaciones de CSP las detecta la guardia de helpers.ts en cada prueba de este archivo.
  await expect(page.locator('svg.login-illustration')).toBeVisible();
});

test('F-03: tras una sesión que expiró, otra persona entra al inicio y no a la ruta de la anterior', async ({ page }) => {
  await uiLogin(page, ADMIN.username, ADMIN.password);
  await page.context().clearCookies();
  await page.evaluate(() => { history.pushState({}, '', '/admin/audit?buscar=algo-de-admin'); dispatchEvent(new PopStateEvent('popstate')); });
  await expect(page.getByRole('status').filter({ hasText: 'Tu sesión expiró' })).toBeVisible();
  await intentar(page, OTRO, PWD);
  await expect(page.getByLabel('Navegación principal')).toBeVisible();
  expect(new URL(page.url()).pathname).toBe('/');
  expect(page.url()).not.toContain('algo-de-admin');
});

test('F-01: si el servidor no confirma el cierre de sesión, la pantalla no dice «Cerraste sesión»', async ({ page }) => {
  await uiLogin(page, OTRO, PWD);
  await page.route('**/api/auth/logout', (route) => route.abort('connectionrefused'));
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: /Cerrar sesión/ }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'No pudimos cerrar la sesión' })).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);
  await expect(page.getByText('Cerraste sesión')).toHaveCount(0);
  // La sesión sigue viva en el servidor: con la red de vuelta, cerrar sí funciona.
  await page.unroute('**/api/auth/logout');
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: /Cerrar sesión/ }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Cerraste sesión' })).toBeVisible();
  expect((await page.context().cookies()).some((c) => c.name === 'erp.auth')).toBe(false);
});
