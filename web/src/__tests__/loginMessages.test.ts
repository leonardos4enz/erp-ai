// Spec 032 (H4): los mensajes del login son para la persona y nunca repiten el texto del servidor; el 401 es uno solo
// (001 F-09) y ni el bloqueo ni el 429 dan cifras.
import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import { messageFor } from '../auth/loginMessages';

const servidor = { title: 'SqlException en db01', detail: 'Usuario admin bloqueado hasta 18:45' };
const texto = (status: number) => { const m = messageFor(new ApiError(status, servidor)); return `${m.title} ${m.detail ?? ''}`; };

describe('messageFor', () => {
  it('no repite el título ni el detalle del servidor en ningún estado', () => {
    for (const status of [0, 400, 401, 403, 404, 409, 423, 429, 500, 502, 503]) {
      expect(texto(status)).not.toMatch(/SqlException|db01|\badmin\b|18:45/);
    }
  });

  it('el 401 es uno solo y menciona el bloqueo sin cifras', () => {
    expect(messageFor(new ApiError(401, {}))).toEqual(messageFor(new ApiError(401, servidor)));
    expect(texto(401)).toContain('se bloquea por unos minutos');
    expect(texto(401)).not.toMatch(/\d/);
  });

  it('el 423 ya no tiene mensaje propio (el servidor nunca lo distingue)', () => {
    expect(messageFor(new ApiError(423, servidor)).title).toBe('No se pudo iniciar sesión');
  });

  it('429 sin cifras; sin respuesta y 5xx como servidor no disponible', () => {
    expect(texto(429)).toMatch(/^Demasiados intentos/);
    expect(texto(429)).not.toMatch(/\d/);
    for (const status of [0, 500, 503]) expect(messageFor(new ApiError(status, servidor)).title).toBe('No pudimos conectar con el servidor');
    expect(messageFor(new Error('x')).title).toBe('No se pudo iniciar sesión');
  });
});
