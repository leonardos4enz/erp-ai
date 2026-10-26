// Spec 032 (revisión adversarial F-02): el 401 de una petición enviada antes de iniciar o cerrar sesión no avisa al
// manejador; si no, echaría a quien acaba de entrar o cambiaría «Cerraste sesión» por «Tu sesión expiró».
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, newSessionGeneration, setUnauthorizedHandler } from '../api/client';

const respuesta401 = () => new Response(JSON.stringify({ title: 'No autenticado' }), { status: 401, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  setUnauthorizedHandler(null);
  vi.unstubAllGlobals();
});

describe('generación de sesión', () => {
  it('un 401 de la sesión actual avisa al manejador', async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.stubGlobal('fetch', vi.fn(async () => respuesta401()));
    await expect(api('/audit')).rejects.toMatchObject({ status: 401 });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('un 401 rezagado de una sesión anterior no avisa', async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    let responder!: (r: Response) => void;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((r) => { responder = r; })));
    const enVuelo = api('/audit');
    newSessionGeneration(); // la persona cerró sesión o volvió a entrar mientras la petición viajaba
    responder(respuesta401());
    await expect(enVuelo).rejects.toMatchObject({ status: 401 });
    expect(handler).not.toHaveBeenCalled();
  });
});
