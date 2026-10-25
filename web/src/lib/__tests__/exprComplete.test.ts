import { describe, expect, it } from 'vitest';
import { apply, complete, completeAfterChoice, fieldsAtPath } from '../exprComplete';
import { scope } from './fixtures';

const at = (text: string) => complete(text, text.length, scope);
const inserts = (text: string) => at(text)?.items.map((i) => i.insert) ?? [];

describe('autocompletado de expresiones (026 H3)', () => {
  it('sugiere campos por nombre o etiqueta y omite los archivados', () => {
    expect(inserts('Cl')).toEqual(['Clave', 'Cliente']);
    expect(inserts('Vie')).toEqual([]);
  });

  it('sigue referencias hasta tres saltos y marca con › lo que se puede seguir', () => {
    expect(inserts('Cliente.Zo')).toEqual(['Cliente.Zona']);
    expect(at('Cliente.Zo')!.items[0].detail).toMatch(/›$/);
    expect(inserts('Cliente.Zona.Cl')).toEqual(['Cliente.Zona.Clave']);
    expect(inserts('Cliente.Zona.Clave.X.Y')).toEqual([]); // más de tres saltos
    expect(inserts('Cliente..N')).toEqual([]);
  });

  it('los campos de usuario ofrecen Nombre y Usuario; las partidas, solo un nivel', () => {
    expect(inserts('Responsable.N')).toEqual(['Responsable.Nombre']);
    expect(fieldsAtPath(scope, ['Partidas'])?.lines).toBe(true);
    expect(fieldsAtPath(scope, ['Partidas', 'Cantidad'])).toBeNull();
  });

  it('dentro de una cadena sugiere las opciones del campo comparado y escapa comillas', () => {
    const text = "Prioridad = '";
    const c = complete(text, text.length, scope)!;
    expect(c.items.map((i) => i.label)).toEqual(['Alta', "D'Media", 'Baja']);
    expect(c.closeQuote).toBe(true);
    const r = apply(text, c, c.items[1]);
    expect(r.text).toBe("Prioridad = 'D''Media'");
    expect(r.caret).toBe(r.text.length);
    expect(complete("Estado = 'En", 12, scope)!.items.map((i) => i.label)).toEqual(['Enviado']);
  });

  it('funciones y palabras clave', () => {
    expect(inserts('IS')).toEqual(['ISBLANK(']);
    expect(inserts('TODA')).toEqual(['TODAY()']);
    expect(inserts('Total > 0 AN')).toContain('AND');
  });

  it('apply reemplaza la palabra entera aunque el cursor esté a mitad', () => {
    const text = 'Clxyz + 1';
    const c = complete(text, 2, scope)!;
    expect(apply(text, c, c.items.find((i) => i.insert === 'Cliente')!).text).toBe('Cliente + 1');
  });

  // Regresión (revisión 026, F-06): tras elegir «Cliente ›» la lista se reabría con el texto del momento de elegir. Si la
  // persona ya había seguido escribiendo («Cliente.Zona.N»), mostraba «Cliente.Zona» en lugar de los campos de Zona.
  it('026 F-06: tras elegir una referencia, la lista se recalcula con el texto actual', () => {
    const chosen = at('Clie')!.items.find((i) => i.insert === 'Cliente')!;
    expect(chosen.detail).toMatch(/›$/);
    const typedMore = 'Cliente.Zona.N';
    const next = completeAfterChoice(chosen, { text: typedMore, caret: typedMore.length }, scope);
    expect(next?.items.map((i) => i.insert)).toEqual(['Cliente.Zona.Nombre']);
    // Un campo simple no reabre la lista.
    const simple = at('Tot')!.items[0];
    expect(completeAfterChoice(simple, { text: 'Total', caret: 5 }, scope)).toBeNull();
    // Abrir una función sugiere aunque no haya palabra empezada (como Ctrl+Espacio).
    const fn = at('IS')!.items[0];
    expect(completeAfterChoice(fn, { text: 'ISBLANK(', caret: 8 }, scope)?.items.length).toBeGreaterThan(0);
  });
});
