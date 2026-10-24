import type { Workflow, WorkflowState, WorkflowTransitionDef } from '../api/types';
import { toIdentifier } from './format';

/**
 * Spec 019: geometría y edición del diagrama del proceso (002). Las posiciones no se guardan: se calculan por capas a
 * partir del estado inicial, así el mismo proceso siempre se dibuja igual y no hay metadato nuevo.
 */

export const NODE_W = 160;
export const NODE_H = 52;
const COL = 250;
const ROW = 92;
const MARGIN_X = 36;
const MARGIN_TOP = 64;

export interface NodeBox { name: string; x: number; y: number; layer: number }
export interface EdgeGeom {
  key: string;
  transition: string;
  from: string;
  to: string;
  path: string;
  /** Centro de la etiqueta, ya acomodada para no tapar estados ni otras etiquetas. */
  labelX: number;
  labelY: number;
  labelText: string;
  labelWidth: number;
}
export const LABEL_H = 22;

export function clip(s: string, n: number) {
  return s.length > n ? `${s.slice(0, n - 1)}…` : s;
}
export interface Diagram { nodes: NodeBox[]; edges: EdgeGeom[]; width: number; height: number }

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/** Capas por distancia desde el inicial; lo que no se alcanza va en una capa final. */
export function layers(w: Workflow): Map<string, number> {
  const names = w.states.map((s) => s.name);
  const out = new Map<string, number>();
  const start = names.find((n) => same(n, w.initial ?? ''));
  if (start) {
    out.set(start, 0);
    const queue = [start];
    while (queue.length) {
      const cur = queue.shift()!;
      for (const t of w.transitions) {
        if (!t.from.some((f) => same(f, cur))) continue;
        const to = names.find((n) => same(n, t.to));
        if (to && !out.has(to)) { out.set(to, out.get(cur)! + 1); queue.push(to); }
      }
    }
  }
  const last = out.size ? Math.max(...out.values()) + 1 : 0;
  for (const n of names) if (!out.has(n)) out.set(n, last);
  return out;
}

const qMid = (s: number, c: number, e: number) => 0.25 * s + 0.5 * c + 0.25 * e;

export function layoutDiagram(w: Workflow): Diagram {
  const layer = layers(w);
  const byLayer = new Map<number, string[]>();
  for (const s of w.states) {
    const l = layer.get(s.name)!;
    byLayer.set(l, [...(byLayer.get(l) ?? []), s.name]);
  }
  const maxRows = Math.max(1, ...[...byLayer.values()].map((x) => x.length));
  const nodes: NodeBox[] = [];
  for (const [l, list] of byLayer) {
    const offset = ((maxRows - list.length) * ROW) / 2;
    list.forEach((name, row) => nodes.push({ name, layer: l, x: MARGIN_X + l * COL, y: MARGIN_TOP + offset + row * ROW }));
  }
  const node = (name: string) => nodes.find((n) => same(n.name, name));

  // Una arista por cada estado de origen; las paralelas (mismo par de estados) se separan.
  const raw: { t: WorkflowTransitionDef; a: NodeBox; b: NodeBox }[] = [];
  for (const t of w.transitions) {
    const b = node(t.to);
    if (!b) continue;
    for (const f of t.from) { const a = node(f); if (a) raw.push({ t, a, b }); }
  }
  const pairKey = (a: string, b: string) => [a, b].sort().join('\u0000');
  const groups = new Map<string, number>();
  for (const r of raw) groups.set(pairKey(r.a.name, r.b.name), (groups.get(pairKey(r.a.name, r.b.name)) ?? 0) + 1);
  const seen = new Map<string, number>();
  let below = 0;
  const edges: Omit<EdgeGeom, 'labelText' | 'labelWidth'>[] = raw.map(({ t, a, b }) => {
    const k = pairKey(a.name, b.name);
    const n = groups.get(k)!;
    const idx = seen.get(k) ?? 0;
    seen.set(k, idx + 1);
    const spread = (idx - (n - 1) / 2) * 30;
    const key = `${t.name}|${a.name}`;
    if (a === b) {
      // Lazo sobre el nodo.
      const h = 40 + idx * 16;
      const x1 = a.x + NODE_W * 0.3; const x2 = a.x + NODE_W * 0.7;
      return { key, transition: t.name, from: a.name, to: b.name, path: `M ${x1} ${a.y} C ${x1} ${a.y - h} ${x2} ${a.y - h} ${x2} ${a.y}`, labelX: a.x + NODE_W / 2, labelY: a.y - h * 0.78 };
    }
    if (b.layer > a.layer) {
      const sx = a.x + NODE_W; const sy = a.y + NODE_H / 2;
      const ex = b.x; const ey = b.y + NODE_H / 2;
      const dx = ex - sx; const dy = ey - sy; const len = Math.hypot(dx, dy) || 1;
      const cx = (sx + ex) / 2 - (dy / len) * spread; const cy = (sy + ey) / 2 + (dx / len) * spread;
      return { key, transition: t.name, from: a.name, to: b.name, path: `M ${sx} ${sy} Q ${cx} ${cy} ${ex} ${ey}`, labelX: qMid(sx, cx, ex), labelY: qMid(sy, cy, ey) };
    }
    if (b.layer === a.layer) {
      // Misma columna: por la derecha, arqueada.
      const down = b.y > a.y;
      const sx = a.x + NODE_W * 0.75; const sy = down ? a.y + NODE_H : a.y;
      const ex = b.x + NODE_W * 0.75; const ey = down ? b.y : b.y + NODE_H;
      const cx = a.x + NODE_W + 40 + Math.abs(spread); const cy = (sy + ey) / 2;
      return { key, transition: t.name, from: a.name, to: b.name, path: `M ${sx} ${sy} Q ${cx} ${cy} ${ex} ${ey}`, labelX: qMid(sx, cx, ex), labelY: qMid(sy, cy, ey) };
    }
    // Regreso a una capa anterior: arco por debajo de los nodos.
    const sx = a.x + NODE_W / 2; const sy = a.y + NODE_H;
    const ex = b.x + NODE_W / 2; const ey = b.y + NODE_H;
    const depth = 56 + Math.abs(spread) + (a.layer - b.layer) * 10;
    const cy = Math.max(sy, ey) + depth;
    below = Math.max(below, cy);
    return { key, transition: t.name, from: a.name, to: b.name, path: `M ${sx} ${sy} Q ${(sx + ex) / 2} ${cy} ${ex} ${ey}`, labelX: (sx + ex) / 2, labelY: qMid(Math.max(sy, ey), cy, Math.max(sy, ey)) };
  });
  const placed = placeLabels(edges.map((e) => {
    const t = w.transitions.find((x) => x.name === e.transition)!;
    const labelText = clip(t.label || t.name, 22);
    return { ...e, labelText, labelWidth: labelText.length * 6.4 + (t.system ? 34 : 18) };
  }), nodes);
  const maxLayer = Math.max(0, ...nodes.map((n) => n.layer));
  // El arco llega a la mitad de la profundidad de su punto de control.
  const nodesBottom = Math.max(0, ...nodes.map((n) => n.y + NODE_H));
  const arcs = below > 0 ? (nodesBottom + below) / 2 + 12 : nodesBottom;
  const height = Math.max(MARGIN_TOP + maxRows * ROW, arcs + 36);
  const labelsBottom = Math.max(0, ...placed.map((e) => e.labelY + LABEL_H / 2 + 8));
  return { nodes, edges: placed, width: MARGIN_X * 2 + maxLayer * COL + NODE_W + 60, height: Math.max(height, labelsBottom) };
}

/**
 * Regresión (revisión exploratoria 019, F-03): con transiciones de nombre largo la etiqueta, centrada en su flecha, tapaba
 * los estados vecinos y a otras etiquetas. Cada una se mueve al primer lugar libre cerca de su flecha (arriba o abajo,
 * en pasos de media etiqueta); si no hay ninguno, se queda en el que menos se encima.
 */
function placeLabels(edges: EdgeGeom[], nodes: NodeBox[]): EdgeGeom[] {
  type Box = { l: number; r: number; t: number; b: number };
  const pad = 4;
  const boxes: Box[] = nodes.map((n) => ({ l: n.x - pad, r: n.x + NODE_W + pad, t: n.y - pad, b: n.y + NODE_H + pad }));
  const overlap = (a: Box, b: Box) => Math.max(0, Math.min(a.r, b.r) - Math.max(a.l, b.l)) * Math.max(0, Math.min(a.b, b.b) - Math.max(a.t, b.t));
  const offsets = [0];
  for (let k = 1; k <= 8; k++) offsets.push(-k * LABEL_H * 0.6, k * LABEL_H * 0.6);
  return edges.map((e) => {
    let best = { y: e.labelY, cost: Infinity };
    for (const dy of offsets) {
      const y = e.labelY + dy;
      if (y - LABEL_H / 2 < 4) continue;
      const box = { l: e.labelX - e.labelWidth / 2, r: e.labelX + e.labelWidth / 2, t: y - LABEL_H / 2, b: y + LABEL_H / 2 };
      const cost = boxes.reduce((sum, o) => sum + overlap(box, o), 0);
      if (cost < best.cost) best = { y, cost };
      if (cost === 0) break;
    }
    boxes.push({ l: e.labelX - e.labelWidth / 2 - pad, r: e.labelX + e.labelWidth / 2 + pad, t: best.y - LABEL_H / 2 - pad, b: best.y + LABEL_H / 2 + pad });
    return { ...e, labelY: best.y };
  });
}

/* ---------------- Edición ---------------- */

export function uniqueName(base: string, taken: string[]): string {
  const used = new Set(taken.map((t) => t.toLowerCase()));
  const root = toIdentifier(base) || 'Estado';
  if (!used.has(root.toLowerCase()) && root.length >= 2) return root;
  for (let n = 2; ; n++) if (!used.has(`${root}${n}`.toLowerCase())) return `${root}${n}`;
}

export function newWorkflow(): Workflow {
  return { initial: 'Borrador', states: [{ name: 'Borrador', label: 'Borrador', color: 'neutral', editable: true }], transitions: [] };
}

export function addState(w: Workflow): { workflow: Workflow; name: string } {
  const name = uniqueName('Nuevo estado', w.states.map((s) => s.name));
  const state: WorkflowState = { name, label: 'Nuevo estado', color: 'neutral', editable: true };
  return { workflow: { ...w, states: [...w.states, state] }, name };
}

export function addTransition(w: Workflow, from: string, to: string): { workflow: Workflow; name: string } {
  const toLabel = w.states.find((s) => same(s.name, to))?.label ?? to;
  const label = `Pasar a ${toLabel}`;
  const name = uniqueName(label, w.transitions.map((t) => t.name));
  const t: WorkflowTransitionDef = { name, label, from: [from], to };
  return { workflow: { ...w, transitions: [...w.transitions, t] }, name };
}

/** Transiciones que se quitan o cambian al eliminar un estado (para el diálogo de confirmación). */
export function stateImpact(w: Workflow, name: string): { removed: WorkflowTransitionDef[]; trimmed: WorkflowTransitionDef[] } {
  const removed: WorkflowTransitionDef[] = [];
  const trimmed: WorkflowTransitionDef[] = [];
  for (const t of w.transitions) {
    const from = t.from.filter((f) => !same(f, name));
    if (same(t.to, name) || from.length === 0) removed.push(t);
    else if (from.length !== t.from.length) trimmed.push(t);
  }
  return { removed, trimmed };
}

export function removeState(w: Workflow, name: string): Workflow {
  const { removed } = stateImpact(w, name);
  return {
    ...w,
    states: w.states.filter((s) => !same(s.name, name)),
    transitions: w.transitions
      .filter((t) => !removed.includes(t))
      .map((t) => (t.from.some((f) => same(f, name)) ? { ...t, from: t.from.filter((f) => !same(f, name)) } : t)),
  };
}

/** Cambia el identificador de un estado nuevo (aún no guardado) y lo actualiza en las transiciones y el inicial. */
export function renameState(w: Workflow, from: string, to: string): Workflow {
  const swap = (n: string) => (same(n, from) ? to : n);
  return {
    initial: swap(w.initial),
    states: w.states.map((s) => (same(s.name, from) ? { ...s, name: to } : s)),
    transitions: w.transitions.map((t) => ({ ...t, from: t.from.map(swap), to: swap(t.to) })),
  };
}

/** Propiedades de un objeto: vacío o `false` (salvo los indicados) quita la propiedad. */
export function patchObject<T extends object>(o: T, patch: Partial<T>, keepFalse: (keyof T)[] = []): T {
  const out = { ...o } as Record<string, unknown>;
  for (const [k, v] of Object.entries(patch)) {
    const empty = v === undefined || v === '' || (Array.isArray(v) && v.length === 0) || (v === false && !keepFalse.includes(k as keyof T));
    if (empty) delete out[k]; else out[k] = v;
  }
  return out as T;
}
