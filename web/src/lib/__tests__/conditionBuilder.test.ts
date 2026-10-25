import { describe, expect, it } from 'vitest';
import { buildCondition, defaultValue, kindOfField, OPS_FOR, parseCondition, type CondGroup } from '../conditionBuilder';
import { pedido, scope } from './fixtures';

const field = (n: string) => pedido.fields.find((f) => f.name === n)!;

describe('constructor de condiciones (026 H3 CA5)', () => {
  it('cada clase de campo tiene sus operadores y un valor inicial', () => {
    expect(kindOfField(field('Total'))).toBe('number');
    expect(kindOfField(field('Estado'))).toBe('choice');
    expect(kindOfField(field('Cliente'))).toBe('reference');
    expect(OPS_FOR.boolean).toEqual(['eq']);
    expect(defaultValue('choice', field('Prioridad'))).toEqual({ kind: 'text', value: 'Alta' });
    expect(defaultValue('choice', field('Estado'), ['Borrador'])).toEqual({ kind: 'text', value: 'Borrador' });
    expect(defaultValue('user')).toEqual({ kind: 'me' });
  });

  it('los renglones se escriben como la expresión que se escribiría a mano', () => {
    const g: CondGroup = { join: 'AND', items: [
      { path: 'Prioridad', op: 'in', value: { kind: 'list', values: ['Alta', "D'Media"] } },
      { path: 'Total', op: 'gte', value: { kind: 'number', value: '1000' } },
      { path: 'Fecha', op: 'lt', value: { kind: 'today', days: -30 } },
      { path: 'Cliente.Zona', op: 'notBlank' },
      { path: 'Responsable', op: 'eq', value: { kind: 'me' } },
      { path: '', op: 'eq' }, // incompleto: se omite
    ] };
    expect(buildCondition(g)).toBe(
      "Prioridad IN ('Alta', 'D''Media') AND Total >= 1000 AND Fecha < ADDDAYS(TODAY(), -30) AND NOT ISBLANK(Cliente.Zona) AND Responsable = CURRENTUSER()");
  });

  it('leer lo que escribe el constructor devuelve los mismos renglones (ida y vuelta)', () => {
    const g: CondGroup = { join: 'OR', items: [
      { path: 'Urgente', op: 'eq', value: { kind: 'bool', value: true } },
      { path: 'Prioridad', op: 'ne', value: { kind: 'text', value: "D'Media" } },
      { path: 'Cliente.Limite', op: 'lte', value: { kind: 'field', value: 'Total' } },
      { path: 'Clave', op: 'blank' },
    ] };
    expect(parseCondition(buildCondition(g), scope)).toEqual(g);
  });

  it('una condición que no tiene la forma simple se queda como texto', () => {
    expect(parseCondition("Total > 0 AND Clave = 'A' OR Urgente = TRUE", scope)).toBeNull(); // mezcla Y con O
    expect(parseCondition('IF(Total > 0, TRUE, FALSE)', scope)).toBeNull();
    expect(parseCondition('Inexistente = 1', scope)).toBeNull();
    expect(parseCondition('Urgente > TRUE', scope)).toBeNull(); // operador que el constructor no ofrece para el tipo
    expect(parseCondition("Clave = 'abierta", scope)).toBeNull();
    expect(parseCondition('   ', scope)).toEqual({ join: 'AND', items: [] });
  });

  it('AND y OR dentro de cadenas o paréntesis no parten la condición', () => {
    expect(parseCondition("Clave = 'A OR B' AND Prioridad IN ('Alta', 'Baja')", scope)?.items).toHaveLength(2);
  });
});
