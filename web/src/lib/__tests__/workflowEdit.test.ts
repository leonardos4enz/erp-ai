import { describe, expect, it } from 'vitest';
import type { Workflow } from '../../api/types';
import { addState, addTransition, layers, layoutDiagram, LABEL_H, NODE_H, NODE_W, patchObject, removeState, renameState, stateImpact, uniqueName } from '../workflowEdit';

const wf = (): Workflow => ({
  initial: 'Borrador',
  states: [{ name: 'Borrador', label: 'Borrador' }, { name: 'Enviado', label: 'Enviado' }, { name: 'Cerrado', label: 'Cerrado' }, { name: 'Huerfano', label: 'Huérfano' }],
  transitions: [
    { name: 'enviar', label: 'Enviar', from: ['Borrador'], to: 'Enviado' },
    { name: 'cerrar', label: 'Cerrar el pedido con un nombre muy largo', from: ['Borrador', 'Enviado'], to: 'Cerrado' },
    { name: 'reabrir', label: 'Reabrir', from: ['Cerrado'], to: 'Borrador' },
  ],
});

describe('diagrama del proceso (019, 026)', () => {
  it('capas por distancia desde el inicial; lo inalcanzable al final', () => {
    expect(Object.fromEntries(layers(wf()))).toEqual({ Borrador: 0, Enviado: 1, Cerrado: 1, Huerfano: 2 });
  });

  it('una flecha por estado de origen, y las etiquetas no tapan estados (019 F-03)', () => {
    const d = layoutDiagram(wf());
    expect(d.edges.map((e) => `${e.transition}:${e.from}>${e.to}`)).toEqual(
      ['enviar:Borrador>Enviado', 'cerrar:Borrador>Cerrado', 'cerrar:Enviado>Cerrado', 'reabrir:Cerrado>Borrador']);
    for (const e of d.edges) {
      for (const n of d.nodes) {
        const x = Math.min(e.labelX + e.labelWidth / 2, n.x + NODE_W) - Math.max(e.labelX - e.labelWidth / 2, n.x);
        const y = Math.min(e.labelY + LABEL_H / 2, n.y + NODE_H) - Math.max(e.labelY - LABEL_H / 2, n.y);
        expect(x > 0 && y > 0, `${e.key} tapa ${n.name}`).toBe(false);
      }
      expect(e.labelY + LABEL_H / 2).toBeLessThanOrEqual(d.height);
    }
    expect(d.edges[1].labelText.length).toBeLessThanOrEqual(22);
  });

  it('quitar un estado quita las transiciones que llegan a él o solo salían de él, y recorta las demás', () => {
    const impact = stateImpact(wf(), 'borrador');
    expect(impact.removed.map((t) => t.name)).toEqual(['enviar', 'reabrir']);
    expect(impact.trimmed.map((t) => t.name)).toEqual(['cerrar']);
    const w = removeState(wf(), 'Borrador');
    expect(w.states.map((s) => s.name)).toEqual(['Enviado', 'Cerrado', 'Huerfano']);
    expect(w.transitions).toEqual([{ name: 'cerrar', label: 'Cerrar el pedido con un nombre muy largo', from: ['Enviado'], to: 'Cerrado' }]);
  });

  it('agregar estados y transiciones con identificadores únicos; renombrar actualiza todo', () => {
    const a = addState(wf());
    expect(addState(a.workflow).name).toBe(`${a.name}2`);
    const t = addTransition(wf(), 'Enviado', 'Huerfano');
    expect(t.workflow.transitions.at(-1)).toMatchObject({ label: 'Pasar a Huérfano', from: ['Enviado'], to: 'Huerfano' });
    expect(t.name).toMatch(/^[A-Za-z][A-Za-z0-9_]+$/);
    const r = renameState(wf(), 'borrador', 'Captura');
    expect(r.initial).toBe('Captura');
    expect(r.transitions.find((x) => x.name === 'reabrir')!.to).toBe('Captura');
    expect(r.transitions.find((x) => x.name === 'cerrar')!.from).toEqual(['Captura', 'Enviado']);
    expect(uniqueName('Enviado', ['enviado'])).toBe('Enviado2');
  });

  it('patchObject quita lo vacío salvo los falsos que se piden conservar', () => {
    expect(patchObject({ name: 'a', editable: true, roles: ['x'] }, { editable: false, roles: [] })).toEqual({ name: 'a' });
    expect(patchObject<{ name: string; editable?: boolean }>({ name: 'a' }, { editable: false }, ['editable'])).toEqual({ name: 'a', editable: false });
  });
});
