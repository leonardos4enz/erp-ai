import { describe, expect, it } from 'vitest';
import type { EntityDef, FieldDef } from '../../api/types';
import {
  compatible, conditionsOf, fromRows, lookupFilterFor, lookupSignature, opLabel, opsFor, sourcePaths, toRows,
} from '../lookupFilter';

const f = (name: string, type: FieldDef['type'], extra: Partial<FieldDef> = {}): FieldDef => ({ name, label: name, type, ...extra } as FieldDef);
const entity = (name: string, fields: FieldDef[]): EntityDef => ({ name, label: name, labelPlural: name, fields } as EntityDef);

const categoria = entity('Categoria', [f('Nombre', 'text')]);
const proveedor = entity('Proveedor', [
  f('Nombre', 'text'), f('Activo', 'boolean'), f('Categorias', 'multireference', { referenceEntity: 'Categoria' }),
]);
const orden = entity('Orden', [
  f('Categoria', 'reference', { referenceEntity: 'Categoria', label: 'Categoría' }),
  f('Proveedor', 'reference', { referenceEntity: 'Proveedor', lookupFilter: { conditions: [{ field: 'Activo', op: 'eq', value: true }, { field: 'Categorias', op: 'contains', source: 'Categoria' }] } }),
]);
const partida = entity('Partida', [
  f('Orden', 'parent', { referenceEntity: 'Orden' }),
  f('Producto', 'reference', { referenceEntity: 'Producto', lookupFilter: { conditions: [{ field: 'Categoria', op: 'in', source: 'Orden.Proveedor.Categorias' }] } }),
]);
const all = [categoria, proveedor, orden, partida];

describe('filtro del buscador (052)', () => {
  it('la forma corta de la 005 es una condición eq', () => {
    expect(conditionsOf({ field: 'Pedido', source: 'Factura.Pedido' })).toEqual([{ field: 'Pedido', op: 'eq', source: 'Factura.Pedido' }]);
    expect(conditionsOf(null)).toEqual([]);
  });

  it('el formulario sabe qué origen falta y cuándo volver a preguntar', () => {
    const prov = orden.fields[1];
    expect(lookupFilterFor(prov, { def: orden, values: {} })).toEqual({ waiting: 'Categoría', key: '[null]' });
    expect(lookupFilterFor(prov, { def: orden, values: { Categoria: 3 } })).toEqual({ waiting: undefined, key: '[3]' });
    // En la partida, el campo padre lleva al encabezado.
    const prod = partida.fields[1];
    expect(lookupFilterFor(prod, { def: partida, values: {} }, { def: orden, values: { Proveedor: 8 } })).toEqual({ waiting: undefined, key: '[8]' });
    expect(lookupFilterFor(prod, { def: partida, values: {} }, { def: orden, values: {} })?.waiting).toBe('Proveedor');
    expect(lookupFilterFor(orden.fields[0], { def: orden, values: {} })).toBeUndefined();
  });

  it('operadores por tipo y tipos compatibles (como el servidor)', () => {
    expect(opsFor(proveedor.fields[1])).toEqual(['eq', 'ne', 'blank', 'notBlank']);
    expect(opsFor(proveedor.fields[2])).toEqual(['contains', 'in', 'blank', 'notBlank']);
    expect(opLabel(proveedor.fields[2], 'in')).toBe('contiene alguno de');
    expect(compatible(proveedor.fields[2], 'contains', orden.fields[0])).toBe(true);
    expect(compatible(proveedor.fields[2], 'contains', orden.fields[1])).toBe(false);
    expect(compatible(f('Categoria', 'reference', { referenceEntity: 'Categoria' }), 'in', proveedor.fields[2])).toBe(true);
    expect(compatible(proveedor.fields[1], 'eq', f('X', 'integer'))).toBe(false);
  });

  it('sugiere rutas de origen compatibles de hasta tres segmentos', () => {
    const target = f('Categoria', 'reference', { referenceEntity: 'Categoria' });
    expect(sourcePaths(partida.fields, all, target, 'in').map((p) => p.path)).toEqual(['Orden.Proveedor.Categorias']);
    expect(sourcePaths(partida.fields, all, target, 'eq').map((p) => p.path)).toEqual(['Orden.Categoria']);
  });

  it('renglones ↔ JSON; sin renglones, quita el filtro', () => {
    const rows = toRows({ field: 'Pedido', source: 'Factura.Pedido', conditions: [{ field: 'Activo', op: 'eq', value: true }] });
    expect(rows.map((r) => r.mode)).toEqual(['source', 'value']);
    expect(fromRows(rows)).toEqual({ conditions: [{ field: 'Pedido', op: 'eq', source: 'Factura.Pedido' }, { field: 'Activo', op: 'eq', value: true }] });
    expect(fromRows([{ field: 'Nombre', op: 'blank', mode: 'value', value: 'x' }])).toEqual({ conditions: [{ field: 'Nombre', op: 'blank' }] });
    expect(fromRows([])).toEqual({ conditions: [] });
    // La forma corta y su equivalente en condiciones no cuentan como cambio.
    expect(lookupSignature({ field: 'Pedido', source: 'Factura.Pedido' })).toBe(lookupSignature({ conditions: [{ field: 'Pedido', op: 'eq', source: 'Factura.Pedido' }] }));
  });
});
