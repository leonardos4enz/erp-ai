import { describe, expect, it } from 'vitest';
import { clean, sameValue } from '../canonical';

describe('comparación canónica del modelador (026)', () => {
  // Regresión (revisión exploratoria 026, F-03): lo guardado trae null que el borrador no; solo abrir una automatización
  // instalada («Póliza de entrada» de Costeo) avisaba «tu cambio queda como modificación local» sin haber cambiado nada.
  it('026 F-03: los nulos del servidor no cuentan como cambio', () => {
    const saved = { name: 'PolizaEntrada', when: null, actions: [{ type: 'create', set: [{ field: 'Cuenta', value: 'X' }], lines: null }] };
    const draft = { actions: [{ set: [{ value: 'X', field: 'Cuenta' }], type: 'create' }], name: 'PolizaEntrada' };
    expect(sameValue(saved, draft)).toBe(true);
  });

  it('el orden de las propiedades no importa; el de los arreglos sí, y un valor distinto es un cambio', () => {
    expect(sameValue({ a: 1, b: { c: 2, d: 3 } }, { b: { d: 3, c: 2 }, a: 1 })).toBe(true);
    expect(sameValue([1, 2], [2, 1])).toBe(false);
    expect(sameValue({ a: 1 }, { a: 2 })).toBe(false);
    expect(sameValue(undefined, null)).toBe(true);
  });

  it('clean quita lo que el servidor omite', () => {
    expect(clean({ a: 'x', b: '', c: null, d: undefined, e: false, f: [], g: 0, h: ['y'] })).toEqual({ a: 'x', g: 0, h: ['y'] });
  });
});
