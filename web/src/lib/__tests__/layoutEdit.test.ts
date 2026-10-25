import { describe, expect, it } from 'vitest';
import type { Layout } from '../../api/types';
import {
  addSection, addTab, dropField, dropSection, findField, initialLayout, itemField, moveField, PALETTE, patchItem, patchSection,
  removeSection, setTabs, stepField, stepSection, stepTab, tabsOf,
} from '../layoutEdit';

const base = (): Layout => ({ sections: [
  { name: 'A', label: 'A', tab: 'Uno', fields: ['Clave', { field: 'Nombre', wide: true, visibleWhen: 'Activo' }] },
  { name: 'B', label: 'B', tab: 'Uno', fields: ['Total'] },
  { name: 'C', label: 'C', tab: 'Dos', columns: 1, fields: [], extra: 'se conserva' } as never,
] });
const names = (l: Layout) => l.sections.map((s) => `${s.name}:${s.fields.map(itemField).join(',')}`);

/** Reglas del servidor: un campo aparece una sola vez y las secciones de una pestaña van juntas. */
function valid(l: Layout) {
  const all = l.sections.flatMap((s) => s.fields.map((f) => itemField(f).toLowerCase()));
  expect(new Set(all).size).toBe(all.length);
  const tabs = l.sections.map((s) => s.tab ?? '');
  for (const t of new Set(tabs)) expect(tabs.slice(tabs.indexOf(t), tabs.lastIndexOf(t) + 1).every((x) => x === t)).toBe(true);
}

describe('diseñador del formulario (019, 026)', () => {
  it('mover y soltar conserva el elemento (ancho y condición) y no duplica', () => {
    const l = dropField(base(), 'nombre', 1, 'Total');
    expect(names(l)).toEqual(['A:Clave', 'B:Nombre,Total', 'C:']);
    expect(l.sections[1].fields[0]).toEqual({ field: 'Nombre', wide: true, visibleWhen: 'Activo' });
    valid(l);
    expect(dropField(base(), 'Clave', 0, 'clave')).toEqual(base()); // soltar sobre sí mismo
    expect(names(moveField(base(), 'Total', PALETTE))).toEqual(['A:Clave,Nombre', 'B:', 'C:']);
  });

  it('con el teclado un campo recorre el formulario en orden de lectura y vuelve a «Sin colocar»', () => {
    let r = stepField(base(), 'Nombre', 1);
    expect(r.pos).toEqual({ section: 1, index: 0 });
    r = stepField(r.layout, 'Nombre', -1);
    expect(r.pos).toEqual({ section: 0, index: 1 });
    r = stepField(moveField(base(), 'Clave', PALETTE), 'Clave', 1);
    expect(r.pos).toEqual({ section: 0, index: 0 });
    r = stepField(r.layout, 'Clave', -1);
    expect(r.pos).toBe(PALETTE);
    expect(findField(r.layout, 'Clave')).toBeNull();
  });

  it('patchItem escribe el campo como texto cuando no tiene ancho ni condición', () => {
    const l = patchItem(patchItem(base(), 'Nombre', { wide: false }), 'Nombre', { visibleWhen: '  ' });
    expect(l.sections[0].fields[1]).toBe('Nombre');
  });

  it('una propiedad vacía o por defecto se quita y lo que el diseñador no conoce se conserva', () => {
    const l = patchSection(base(), 2, { description: '', columns: 2 });
    expect(l.sections[2]).not.toHaveProperty('columns');
    expect(l.sections[2]).not.toHaveProperty('description');
    expect((l.sections[2] as unknown as { extra: string }).extra).toBe('se conserva');
  });

  it('las secciones de una pestaña nunca quedan separadas', () => {
    const down = stepSection(base(), 1, 1); // la última de «Uno» al bajar pasa a «Dos» en lugar de saltar
    expect(down.layout.sections[1].tab).toBe('Dos');
    valid(down.layout);
    expect(names(stepSection(base(), 0, 1).layout)).toEqual(['B:Total', 'A:Clave,Nombre', 'C:']);
    valid(dropSection(base(), 0, { tab: 'Dos' }));
    expect(dropSection(base(), 2, { before: 0 }).sections[0].tab).toBe('Uno');
    valid(dropSection(base(), 2, { before: 0 }));
    expect(tabsOf(stepTab(base(), 'Dos', -1))).toEqual(['Dos', 'Uno']);
    valid(stepTab(base(), 'Dos', -1));
  });

  it('agregar y quitar secciones y pestañas con nombres únicos', () => {
    const a = addSection(base(), 'Uno');
    expect(a.index).toBe(2);
    expect(a.layout.sections[2]).toMatchObject({ name: 'Seccion', label: 'Nueva sección', tab: 'Uno', fields: [] });
    valid(a.layout);
    expect(addSection(a.layout, 'Uno').layout.sections[3]).toMatchObject({ name: 'Seccion2', label: 'Nueva sección 2' });
    expect(addTab(base()).tab).toBe('Nueva pestaña');
    expect(names(removeSection(base(), 0))).toEqual(['B:Total', 'C:']);
    expect(setTabs(initialLayout(['X']), true).sections[0].tab).toBe('General');
    expect(setTabs(setTabs(initialLayout(['X']), true), false).sections[0]).not.toHaveProperty('tab');
  });
});
