// Spec 030 (H5 CA4): cada pantalla de la SPA tiene captura de referencia o una razón escrita de por qué no (011 F-05: el
// asistente se escapó a las capturas porque no tenía referencia; 024 F-04: referencias que ya no protegían nada). Una ruta
// nueva en App.tsx hace fallar esta prueba hasta que se declare aquí. Las que no tienen captura se listan como aviso.
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const web = path.resolve(import.meta.dirname, '..', '..');
const capturas = path.join(web, 'e2e', '__screenshots__');

type Pantalla = { capturas: string[] } | { sinCaptura: string };

/** Ruta de App.tsx → capturas que la protegen (archivo › nombre) o por qué no tiene. */
export const PANTALLAS: Record<string, Pantalla> = {
  '/login': { capturas: ['visual.spec.ts/login-light.png', 'visual.spec.ts/login-dark.png', 'visual.spec.ts/login-movil.png', 'visual.spec.ts/login-error-light.png', 'visual.spec.ts/login-error-dark.png'] },
  '/': { capturas: ['navegacion.spec.ts/inicio-light.png', 'navegacion.spec.ts/inicio-dark.png'] },
  'm/:app': { sinCaptura: 'misma plantilla que el inicio (navegacion.spec.ts › inicio-*), filtrada por módulo' },
  admin: { capturas: ['navegacion.spec.ts/configuracion-light.png', 'navegacion.spec.ts/configuracion-dark.png'] },
  'c/:entity': { sinCaptura: 'lista genérica; la cubren axe y las guardias en compras, interfaz y seguridad (riesgo R-030-3)' },
  'c/:entity/new': { capturas: ['visual.spec.ts/formulario-light.png', 'visual.spec.ts/formulario-dark.png', 'visual.spec.ts/formulario-movil.png'] },
  'c/:entity/:id': { capturas: ['valuacion-rutas-avisos.spec.ts/aviso-formulario-light.png', 'valuacion-rutas-avisos.spec.ts/aviso-formulario-dark.png'] },
  reports: { sinCaptura: 'lista de reportes; la cubren axe y las guardias en interfaz.spec.ts (riesgo R-030-3)' },
  'reports/:entity/:name': { sinCaptura: 'gráfica y tabla con datos de cada corrida; interfaz.spec.ts (riesgo R-030-3)' },
  dashboards: { sinCaptura: 'lista de tableros; interfaz.spec.ts (riesgo R-030-3)' },
  'dashboards/:name': { sinCaptura: 'tablero con datos de cada corrida; interfaz.spec.ts (riesgo R-030-3)' },
  'admin/model': { capturas: ['visual.spec.ts/modelador-light.png', 'visual.spec.ts/modelador-dark.png',
    'visual-modelador.spec.ts/catalogos-por-modulo-light.png', 'visual-modelador.spec.ts/automatizaciones-light.png'] },
  'admin/explorer': { capturas: ['explorador.spec.ts/explorador-bd-light.png', 'explorador.spec.ts/explorador-bd-dark.png'] },
  'admin/images': { sinCaptura: 'galería de imágenes subidas en la corrida; visual-pdf.spec.ts (riesgo R-030-3)' },
  'admin/security': { sinCaptura: 'tablas de usuarios y roles que cambian con cada archivo de la suite (riesgo R-030-3)' },
  'admin/modules': { sinCaptura: 'tarjetas de módulos con su versión instalada; compras.spec.ts (riesgo R-030-3)' },
  'admin/integrations': { sinCaptura: 'webhooks, claves y clientes OAuth de la corrida; integraciones.spec.ts (riesgo R-030-3)' },
  'admin/automations': { sinCaptura: 'bandeja con fechas y duraciones de la corrida; programadas.spec.ts (riesgo R-030-3)' },
  'admin/environments': { sinCaptura: 'paquetes e historial de la corrida; entornos.spec.ts (riesgo R-030-3)' },
  'admin/companies': { sinCaptura: 'empresas y tipos de cambio de la corrida; multiempresa.spec.ts (riesgo R-030-3)' },
  'admin/assistant': { sinCaptura: 'conversación simulada; asistente.spec.ts compara sus márgenes con Entornos (011 F-05)' },
  'admin/audit': { sinCaptura: 'bitácora con fechas de la corrida; seguridad.spec.ts (riesgo R-030-3)' },
  '*': { sinCaptura: 'página de «no encontrado» de una línea' },
};

/** Rutas declaradas en App.tsx (`<Route path="…">`). */
export function rutasDe(app: string): string[] {
  const rutas = [...app.matchAll(/<Route\s+path="([^"]+)"/g)].map((m) => m[1]);
  // La ruta índice (`<Route index …>`) es el inicio.
  return /<Route\s+index\b/.test(app) ? ['/', ...rutas] : rutas;
}

describe('pantallas de la SPA y sus capturas de referencia (030 H5 CA4)', () => {
  const rutas = rutasDe(readFileSync(path.join(web, 'src', 'App.tsx'), 'utf8'));

  it('lee las rutas de App.tsx', () => {
    expect(rutas.length).toBeGreaterThan(15);
    expect(rutas).toContain('admin/model');
  });

  it('cada ruta tiene captura o una razón escrita, y no sobran declaraciones', () => {
    expect(rutas.filter((r) => !(r in PANTALLAS)), 'rutas nuevas sin captura ni razón: decláralas en PANTALLAS').toEqual([]);
    expect(Object.keys(PANTALLAS).filter((r) => !rutas.includes(r)), 'declaraciones de rutas que ya no existen').toEqual([]);
  });

  it('las capturas declaradas existen', () => {
    const faltan = Object.values(PANTALLAS).flatMap((p) => ('capturas' in p ? p.capturas : [])).filter((c) => !existsSync(path.join(capturas, c)));
    expect(faltan).toEqual([]);
  });

  it('aviso: pantallas sin captura de referencia', () => {
    const sin = Object.entries(PANTALLAS).filter(([, p]) => 'sinCaptura' in p);
    console.warn(`[pantallas] ${sin.length} de ${rutas.length} pantallas sin captura de referencia:\n` +
      sin.map(([r, p]) => `  ${r}: ${(p as { sinCaptura: string }).sinCaptura}`).join('\n'));
    for (const [, p] of sin) expect((p as { sinCaptura: string }).sinCaptura.length).toBeGreaterThan(10);
  });
});
