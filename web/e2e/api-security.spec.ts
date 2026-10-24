// Pruebas de seguridad contra la API real (spec 001, plan.md › STRIDE).
// Reemplazan al script PowerShell usado en la primera verificación.
import { ADMIN, anonApi, apiAs, applyPlan, expect, roleId, test } from './helpers';

const ENTITY = 'ApiEmpaque';
const plan = {
  description: 'Catálogo para pruebas de API',
  operations: [{
    op: 'createEntity', name: ENTITY, label: 'Empaque API', labelPlural: 'Empaques API', icon: 'package',
    fields: [
      { name: 'Clave', label: 'Clave', type: 'text', maxLength: 20, unique: true, required: true },
      { name: 'Nombre', label: 'Nombre', type: 'text', requiredForRoles: ['Administrador'] },
      { name: 'Material', label: 'Material', type: 'choice', options: ['Cartón', 'Plástico', 'Madera'] },
      { name: 'Capacidad', label: 'Capacidad', type: 'decimal', precision: 10, scale: 2 },
    ],
  }],
};

test.describe.configure({ mode: 'serial' });

test.describe('Autenticación y sesión', () => {
  test('rechaza peticiones que modifican estado sin la cabecera anti-CSRF', async () => {
    const api = await anonApi({});
    const res = await api.post('/api/auth/login', { data: ADMIN });
    expect(res.status()).toBe(403);
  });

  test('el login fallido no revela si el usuario existe', async () => {
    const api = await anonApi();
    const missing = await api.post('/api/auth/login', { data: { username: 'no-existe', password: 'una contraseña cualquiera' } });
    const wrong = await api.post('/api/auth/login', { data: { username: 'admin', password: 'una contraseña incorrecta' } });
    expect(missing.status()).toBe(401);
    expect(wrong.status()).toBe(401);
    expect((await missing.json()).title).toBe((await wrong.json()).title);
  });

  test('sin sesión la API responde 401', async () => {
    const res = await (await anonApi()).get('/api/auth/me');
    expect(res.status()).toBe(401);
  });

  test('la cookie de sesión es HttpOnly, Secure y SameSite=Strict, con cabeceras de endurecimiento', async () => {
    const res = await (await anonApi()).post('/api/auth/login', { data: ADMIN });
    expect(res.status()).toBe(200);
    const cookie = res.headers()['set-cookie'].toLowerCase();
    expect(cookie).toContain('httponly');
    expect(cookie).toContain('secure');
    expect(cookie).toContain('samesite=strict');
    const h = res.headers();
    expect(h['content-security-policy']).toContain("frame-ancestors 'none'");
    expect(h['x-content-type-options']).toBe('nosniff');
    expect(h['cache-control']).toContain('no-store');
    expect(h['server']).toBeUndefined();
  });
});

test.describe('Planes de cambios', () => {
  test('la vista previa valida y aplicar crea el catálogo', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const preview = await (await api.post('/api/model/plan/preview', { data: plan })).json();
    expect(preview.valid).toBe(true);
    expect(preview.steps[0].sql).toContain(`CREATE TABLE [app].[${ENTITY}]`);
    await applyPlan(api, plan);
  });

  test('rechaza inyección SQL en nombres de campo', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const res = await api.post('/api/model/plan/apply', {
      data: { description: 'x', operations: [{ op: 'addField', entity: ENTITY, field: { name: 'x] int; DROP TABLE meta.[User];--', label: 'x', type: 'text' } }] },
    });
    expect(res.status()).toBe(400);
  });

  test('rechaza tipos de operación desconocidos', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const res = await api.post('/api/model/plan/apply', { data: { description: 'x', operations: [{ op: 'dropTable', entity: ENTITY }] } });
    expect(res.status()).toBe(400);
  });
});

test.describe('Datos', () => {
  test('aplica la regla "obligatorio para rol"', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const res = await api.post(`/api/data/${ENTITY}`, { data: { Clave: 'CJ-01', Material: 'Cartón' } });
    expect(res.status()).toBe(400);
    expect((await res.json()).errors.Nombre).toBeTruthy();
  });

  test('valida únicos, opciones y campos desconocidos', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    expect((await api.post(`/api/data/${ENTITY}`, { data: { Clave: 'CJ-01', Nombre: 'Caja chica', Material: 'Cartón', Capacidad: 12.5 } })).status()).toBe(201);

    const dup = await api.post(`/api/data/${ENTITY}`, { data: { Clave: 'CJ-01', Nombre: 'Otra' } });
    expect(dup.status()).toBe(400);
    expect((await dup.json()).errors.Clave).toBeTruthy();

    const bad = await api.post(`/api/data/${ENTITY}`, { data: { Clave: 'CJ-02', Nombre: 'X', Material: 'Acero', Hack: 1 } });
    const errors = (await bad.json()).errors;
    expect(bad.status()).toBe(400);
    expect(errors.Material).toBeTruthy();
    expect(errors.Hack).toBeTruthy();
  });

  test('neutraliza inyección en orden y búsqueda', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const evil = await api.get(`/api/data/${ENTITY}`, { params: { sort: 'Id;DROP TABLE x', search: "' OR 1=1--" } });
    expect(evil.status()).toBe(200);
    expect((await evil.json()).total).toBe(0);
    const ok = await api.get(`/api/data/${ENTITY}`, { params: { search: 'chica' } });
    expect((await ok.json()).total).toBe(1);
    const wildcard = await api.get(`/api/data/${ENTITY}`, { params: { search: '%' } });
    expect((await wildcard.json()).total, 'el % se trata como texto literal').toBe(0);
  });

  test('ordena por Id en los dos sentidos', async () => {
    // Regresión (revisión exploratoria 022, F-01): sort=Id repetía la columna del desempate y respondía 500.
    const api = await apiAs(ADMIN.username, ADMIN.password);
    for (const dir of ['asc', 'desc'] as const) {
      for (const path of ['', '/export']) {
        const res = await api.get(`/api/data/${ENTITY}${path}`, { params: { sort: 'Id', dir } });
        expect(res.status(), `${path || 'listado'} ${dir}`).toBe(200);
      }
      const ids = ((await (await api.get(`/api/data/${ENTITY}`, { params: { sort: 'Id', dir } })).json()).items as { id: number }[]).map((r) => r.id);
      expect(ids.length).toBeGreaterThan(0);
      expect(ids).toEqual([...ids].sort((a, b) => (dir === 'asc' ? a - b : b - a)));
    }
  });

  test('detecta ediciones concurrentes (rowversion)', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const row = (await (await api.get(`/api/data/${ENTITY}`, { params: { search: 'CJ-01' } })).json()).items[0];
    const first = await api.put(`/api/data/${ENTITY}/${row.id}`, { data: { Nombre: 'v2', _rowVersion: row._rowVersion } });
    const second = await api.put(`/api/data/${ENTITY}/${row.id}`, { data: { Nombre: 'v3', _rowVersion: row._rowVersion } });
    expect(first.status()).toBe(200);
    expect(second.status()).toBe(409);
  });

  test('solo expone catálogos registrados en metadatos', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    expect((await api.get('/api/data/meta.User')).status()).toBe(404);
    expect((await api.get('/api/data/..%2Fmeta')).status()).toBe(404);
  });

  test('el borrado es lógico y queda auditado', async () => {
    const api = await apiAs(ADMIN.username, ADMIN.password);
    const created = await (await api.post(`/api/data/${ENTITY}`, { data: { Clave: 'BORRAR', Nombre: 'Temporal' } })).json();
    expect((await api.delete(`/api/data/${ENTITY}/${created.id}`)).status()).toBe(204);
    expect((await api.get(`/api/data/${ENTITY}/${created.id}`)).status()).toBe(404);
    const audit = await (await api.get('/api/audit', { params: { entity: ENTITY, action: 'delete' } })).json();
    expect(audit.items.some((i: { recordId: string }) => i.recordId === String(created.id))).toBe(true);
  });
});

test.describe('Autorización y bloqueo', () => {
  const user = { username: 'api-capturista', password: 'una frase larga de prueba 2026' };

  test('un usuario sin permisos recibe 403 en datos, modelo, usuarios y auditoría', async () => {
    const admin = await apiAs(ADMIN.username, ADMIN.password);
    const created = await admin.post('/api/admin/users', {
      data: { ...user, displayName: 'Capturista API', roleIds: [await roleId(admin, 'Usuario')] },
    });
    expect(created.status()).toBe(201);

    const api = await apiAs(user.username, user.password);
    expect((await api.get(`/api/data/${ENTITY}`)).status()).toBe(403);
    expect((await api.post('/api/model/plan/apply', { data: plan })).status()).toBe(403);
    expect((await api.get('/api/admin/users')).status()).toBe(403);
    expect((await api.get('/api/audit')).status()).toBe(403);
    const me = await (await api.get('/api/auth/me')).json();
    expect(me.entities[ENTITY]).toBeUndefined();
  });

  test('cambiar la contraseña revoca las sesiones abiertas', async () => {
    const admin = await apiAs(ADMIN.username, ADMIN.password);
    const session = await apiAs(user.username, user.password);
    const u = (await (await admin.get('/api/admin/users')).json()).find((x: { username: string }) => x.username === user.username);
    user.password = 'otra frase larga de prueba 2026';
    expect((await admin.put(`/api/admin/users/${u.id}`, {
      data: { displayName: u.displayName, isActive: true, roleIds: u.roleIds, password: user.password },
    })).status()).toBe(200);
    expect((await session.get('/api/auth/me')).status()).toBe(401);
  });

  test('la política rechaza contraseñas débiles o que contienen el usuario', async () => {
    const admin = await apiAs(ADMIN.username, ADMIN.password);
    for (const password of ['corta', 'mi-clave-api-debil-2026']) {
      const res = await admin.post('/api/admin/users', { data: { username: 'api-debil', displayName: 'x', password, roleIds: [] } });
      expect(res.status()).toBe(400);
    }
  });

  test('tras 5 intentos fallidos la cuenta se bloquea aunque la contraseña sea correcta', async () => {
    const api = await anonApi();
    for (let i = 0; i < 5; i++) await api.post('/api/auth/login', { data: { username: user.username, password: 'incorrecta incorrecta' } });
    const res = await api.post('/api/auth/login', { data: user });
    expect(res.status()).toBe(401);
  });

  test('no permite dejar el sistema sin administradores', async () => {
    const admin = await apiAs(ADMIN.username, ADMIN.password);
    const me = await (await admin.get('/api/auth/me')).json();
    const res = await admin.put(`/api/admin/users/${me.id}`, { data: { displayName: 'Administrador', isActive: true, roleIds: [] } });
    expect(res.status()).toBe(409);
  });
});
