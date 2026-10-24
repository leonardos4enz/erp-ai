import type { Layout, LayoutItem, LayoutSection } from '../api/types';

/**
 * Spec 019: operaciones del diseñador del formulario sobre un layout de la 007. Son puras: devuelven un layout nuevo y
 * conservan tal cual las propiedades que no tocan (lo que el diseñador no conoce no se pierde). Por construcción
 * mantienen las reglas del servidor: las secciones de una pestaña van juntas y un campo aparece una sola vez.
 */

export const itemField = (i: LayoutItem) => (typeof i === 'string' ? i : i.field);
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

export type FieldPos = { section: number; index: number };
export const PALETTE = 'palette' as const;

export function findField(l: Layout, name: string): FieldPos | null {
  for (let s = 0; s < l.sections.length; s++) {
    const index = l.sections[s].fields.findIndex((i) => same(itemField(i), name));
    if (index >= 0) return { section: s, index };
  }
  return null;
}

function withFields(s: LayoutSection, fields: LayoutItem[]): LayoutSection {
  return { ...s, fields };
}

/** Quita el campo de donde esté y devuelve el layout sin él y su elemento (con su condición y ancho). */
function take(l: Layout, name: string): { layout: Layout; item: LayoutItem } {
  const pos = findField(l, name);
  if (!pos) return { layout: l, item: name };
  const item = l.sections[pos.section].fields[pos.index];
  const sections = l.sections.map((s, i) => (i === pos.section ? withFields(s, s.fields.filter((_, j) => j !== pos.index)) : s));
  return { layout: { ...l, sections }, item };
}

function put(l: Layout, item: LayoutItem, to: FieldPos): Layout {
  const sections = l.sections.map((s, i) => {
    if (i !== to.section) return s;
    const fields = s.fields.slice();
    fields.splice(Math.max(0, Math.min(to.index, fields.length)), 0, item);
    return withFields(s, fields);
  });
  return { ...l, sections };
}

/** Mueve un campo a una posición (índice contado sin el campo) o lo quita del layout («Sin colocar»). */
export function moveField(l: Layout, name: string, to: FieldPos | typeof PALETTE): Layout {
  const { layout, item } = take(l, name);
  return to === PALETTE ? layout : put(layout, item, to);
}

/** Arrastrar y soltar: el campo queda antes de `before` en la sección, o al final si no hay `before`. */
export function dropField(l: Layout, name: string, section: number, before: string | null): Layout {
  if (before && same(before, name)) return l;
  const { layout, item } = take(l, name);
  const fields = layout.sections[section]?.fields ?? [];
  const at = before ? fields.findIndex((i) => same(itemField(i), before)) : -1;
  return put(layout, item, { section, index: at >= 0 ? at : fields.length });
}

/**
 * Un paso de teclado (↑ = -1, ↓ = +1) en el orden de lectura del formulario: dentro de la sección, y en el borde pasa
 * a la sección vecina. Desde «Sin colocar», ↓ lo lleva al inicio de la primera sección; desde ahí, ↑ lo regresa.
 */
export function stepField(l: Layout, name: string, dir: -1 | 1): { layout: Layout; pos: FieldPos | typeof PALETTE } {
  const pos = findField(l, name);
  if (!pos) {
    if (dir === 1 && l.sections.length) return { layout: moveField(l, name, { section: 0, index: 0 }), pos: { section: 0, index: 0 } };
    return { layout: l, pos: PALETTE };
  }
  const len = l.sections[pos.section].fields.length;
  let to: FieldPos | typeof PALETTE;
  if (dir === 1) {
    if (pos.index < len - 1) to = { section: pos.section, index: pos.index + 1 };
    else if (pos.section < l.sections.length - 1) to = { section: pos.section + 1, index: 0 };
    else return { layout: l, pos };
  } else if (pos.index > 0) to = { section: pos.section, index: pos.index - 1 };
  else if (pos.section > 0) to = { section: pos.section - 1, index: l.sections[pos.section - 1].fields.length };
  else to = PALETTE;
  return { layout: moveField(l, name, to), pos: to };
}

/** Ancho completo y condición de un campo; sin ninguno se escribe como texto, igual que en los módulos. */
export function patchItem(l: Layout, name: string, patch: { wide?: boolean; visibleWhen?: string }): Layout {
  const pos = findField(l, name);
  if (!pos) return l;
  const old = l.sections[pos.section].fields[pos.index];
  const base: Exclude<LayoutItem, string> = typeof old === 'string' ? { field: old } : { ...old };
  if ('wide' in patch) { if (patch.wide) base.wide = true; else delete base.wide; }
  if ('visibleWhen' in patch) { if (patch.visibleWhen?.trim()) base.visibleWhen = patch.visibleWhen; else delete base.visibleWhen; }
  const item: LayoutItem = Object.keys(base).length === 1 ? base.field : base;
  return { ...l, sections: l.sections.map((s, i) => (i === pos.section ? withFields(s, s.fields.map((x, j) => (j === pos.index ? item : x))) : s)) };
}

/* ---------------- Secciones y pestañas ---------------- */

export const usesTabs = (l: Layout) => l.sections.some((s) => !!s.tab);

/** Pestañas en orden de aparición. */
export function tabsOf(l: Layout): string[] {
  return [...new Set(l.sections.map((s) => s.tab).filter((t): t is string => !!t))];
}

export function uniqueName(base: string, taken: string[]): string {
  const used = new Set(taken.map((t) => t.toLowerCase()));
  const root = base || 'Seccion';
  if (!used.has(root.toLowerCase())) return root;
  for (let n = 2; ; n++) if (!used.has(`${root}${n}`.toLowerCase())) return `${root}${n}`;
}

export function uniqueLabel(base: string, taken: string[]): string {
  const used = new Set(taken.map((t) => t.toLowerCase()));
  if (!used.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) if (!used.has(`${base} ${n}`.toLowerCase())) return `${base} ${n}`;
}

/** Propiedades de una sección; un valor vacío quita la propiedad (así no aparece «Revisar cambios» por un ""). */
export function patchSection(l: Layout, index: number, patch: Partial<Omit<LayoutSection, 'fields'>>): Layout {
  return {
    ...l,
    sections: l.sections.map((s, i) => {
      if (i !== index) return s;
      const out: LayoutSection = { ...s };
      for (const [k, v] of Object.entries(patch) as [keyof LayoutSection, unknown][]) {
        if (v === undefined || v === '' || (k === 'columns' && v === 2)) delete out[k];
        else (out as unknown as Record<string, unknown>)[k] = v;
      }
      return out;
    }),
  };
}

/** Sección nueva al final de la pestaña indicada (o del layout). */
export function addSection(l: Layout, tab?: string): { layout: Layout; index: number } {
  const label = uniqueLabel('Nueva sección', l.sections.map((s) => s.label));
  const section: LayoutSection = { name: uniqueName('Seccion', l.sections.map((s) => s.name)), label, ...(tab ? { tab } : {}), fields: [] };
  const sections = l.sections.slice();
  let at = sections.length;
  if (tab) { const last = sections.map((s) => s.tab).lastIndexOf(tab); if (last >= 0) at = last + 1; }
  sections.splice(at, 0, section);
  return { layout: { ...l, sections }, index: at };
}

/** Quita la sección; sus campos vuelven a «Sin colocar» (el formulario los muestra en «Otros datos»). */
export function removeSection(l: Layout, index: number): Layout {
  return { ...l, sections: l.sections.filter((_, i) => i !== index) };
}

/**
 * Sube o baja una sección. Si es la primera (o la última) de su pestaña, en lugar de saltar a otra posición cambia a la
 * pestaña vecina: así las secciones de una pestaña nunca quedan separadas.
 */
export function stepSection(l: Layout, index: number, dir: -1 | 1): { layout: Layout; index: number } {
  const j = index + dir;
  if (j < 0 || j >= l.sections.length) return { layout: l, index };
  const s = l.sections[index];
  const neighbour = l.sections[j];
  if (usesTabs(l) && neighbour.tab !== s.tab) {
    return { layout: { ...l, sections: l.sections.map((x, i) => (i === index ? { ...x, tab: neighbour.tab } : x)) }, index };
  }
  const sections = l.sections.slice();
  [sections[index], sections[j]] = [sections[j], sections[index]];
  return { layout: { ...l, sections }, index: j };
}

/** Arrastrar y soltar una sección: antes de otra (toma su pestaña) o al final de una pestaña. */
export function dropSection(l: Layout, from: number, target: { before: number } | { tab: string } | { end: true }): Layout {
  if ('before' in target && (target.before === from)) return l;
  const moving = l.sections[from];
  const rest = l.sections.filter((_, i) => i !== from);
  let at = rest.length;
  let tab = moving.tab;
  if ('before' in target) {
    const anchor = l.sections[target.before];
    at = rest.indexOf(anchor);
    tab = anchor.tab;
  } else if ('tab' in target) {
    const last = rest.map((s) => s.tab).lastIndexOf(target.tab);
    at = last >= 0 ? last + 1 : rest.length;
    tab = target.tab;
  }
  const placed = usesTabs(l) ? { ...moving, tab } : moving;
  rest.splice(at, 0, placed);
  return { ...l, sections: rest };
}

/** Organizar en pestañas: todas las secciones en «General»; al desactivar se quitan todas. */
export function setTabs(l: Layout, on: boolean): Layout {
  return {
    ...l,
    sections: l.sections.map((s) => {
      if (on) return s.tab ? s : { ...s, tab: 'General' };
      const { tab: _drop, ...rest } = s;
      return rest;
    }),
  };
}

export function renameTab(l: Layout, from: string, to: string): Layout {
  return { ...l, sections: l.sections.map((s) => (s.tab === from ? { ...s, tab: to } : s)) };
}

/** Mueve el bloque de secciones de una pestaña antes o después de la pestaña vecina. */
export function stepTab(l: Layout, tab: string, dir: -1 | 1): Layout {
  const tabs = tabsOf(l);
  const i = tabs.indexOf(tab);
  const j = i + dir;
  if (i < 0 || j < 0 || j >= tabs.length) return l;
  [tabs[i], tabs[j]] = [tabs[j], tabs[i]];
  return { ...l, sections: tabs.flatMap((t) => l.sections.filter((s) => s.tab === t)) };
}

/** Pestaña nueva con una sección vacía. */
export function addTab(l: Layout): { layout: Layout; tab: string; index: number } {
  const tab = uniqueLabel('Nueva pestaña', tabsOf(l));
  const r = addSection(l, tab);
  return { ...r, tab };
}

/** Quita la pestaña con sus secciones; sus campos vuelven a «Sin colocar». */
export function removeTab(l: Layout, tab: string): Layout {
  return { ...l, sections: l.sections.filter((s) => s.tab !== tab) };
}

/** Layout inicial: una sección con los campos en su orden actual. */
export function initialLayout(fields: string[]): Layout {
  return { sections: [{ name: 'General', label: 'Datos generales', fields }] };
}
