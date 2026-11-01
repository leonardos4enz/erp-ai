// Spec 041: lo que el explorador decide sin el servidor (qué falta para pedir el resultado y la definición mínima).
import { describe, expect, it } from 'vitest';
import { clean, missingPart } from '../personalReport';

describe('explorador de reportes (041)', () => {
  it('no pide el resultado mientras falta un campo o una expresión', () => {
    expect(missingPart({ entity: 'P', measures: [{ fn: 'count' }] })).toBeNull();
    expect(missingPart({ entity: 'P', measures: [] })).toBe('Agrega una medida.');
    expect(missingPart({ entity: 'P', groupBy: [{ field: 'Zona' }, { field: '' }], measures: [{ fn: 'count' }] })).toBe('Elige el campo de la agrupación 2.');
    expect(missingPart({ entity: 'P', measures: [{ fn: 'count' }, { fn: 'sum' }] })).toBe('Elige el campo de la medida 2.');
    expect(missingPart({ entity: 'P', measures: [{ fn: 'sum', field: 'Monto' }, { fn: 'calc', expr: '  ' }] })).toBe('Escribe la expresión de la medida 2.');
    // El conteo no necesita campo; la agrupación vacía se reporta antes que la medida.
    expect(missingPart({ entity: 'P', groupBy: [{ field: '' }], measures: [{ fn: 'sum' }] })).toBe('Elige el campo de la agrupación 1.');
  });

  it('la definición guardada no lleva claves vacías', () => {
    expect(clean({ entity: 'P', chart: undefined, filters: [], label: '', limit: 10, measures: [{ fn: 'count' }] }))
      .toEqual({ entity: 'P', limit: 10, measures: [{ fn: 'count' }] });
  });
});
