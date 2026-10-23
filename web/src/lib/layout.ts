import type { EntityDef, FieldDef, LayoutItem, LayoutState } from '../api/types';

/** Spec 007: sección del formulario ya resuelta para el usuario (sin campos ocultos por seguridad o por condición). */
export interface FormSection {
  name: string;
  label: string;
  description?: string;
  tab?: string;
  columns: 1 | 2;
  fields: { field: FieldDef; wide: boolean }[];
}

export const OTHER_SECTION = '__otros';

const itemField = (i: LayoutItem) => (typeof i === 'string' ? i : i.field);
const itemWide = (i: LayoutItem) => typeof i !== 'string' && !!i.wide;

/** ¿El layout tiene condiciones? Entonces el formulario pide al servidor qué ocultar mientras se captura. */
export function hasLayoutConditions(def: EntityDef): boolean {
  return !!def.layout?.sections.some((s) => s.visibleWhen || s.fields.some((f) => typeof f !== 'string' && f.visibleWhen));
}

/**
 * Secciones del formulario según el layout. `candidates` son los campos del encabezado que la persona puede ver
 * (la seguridad de la 006 ya los filtró). Los que el layout no menciona van a «Otros datos», salvo los que
 * `unplacedElsewhere` acepte (en un documento, las fórmulas siguen en la tarjeta de totales).
 * Devuelve `null` si el catálogo no tiene layout.
 */
export function formSections(
  def: EntityDef,
  candidates: FieldDef[],
  hidden: LayoutState | null,
  unplacedElsewhere: (f: FieldDef) => boolean = () => false,
): FormSection[] | null {
  if (!def.layout?.sections.length) return null;
  const byName = new Map(candidates.map((f) => [f.name.toLowerCase(), f]));
  const hiddenSections = new Set((hidden?.hiddenSections ?? []).map((s) => s.toLowerCase()));
  const hiddenFields = new Set((hidden?.hiddenFields ?? []).map((s) => s.toLowerCase()));
  const placed = new Set<string>();
  const out: FormSection[] = [];
  // Mientras el servidor no responde, lo condicional no se muestra (así no aparece y desaparece al abrir).
  const pending = hidden === null;
  for (const s of def.layout.sections) {
    for (const i of s.fields) placed.add(itemField(i).toLowerCase());
    if (hiddenSections.has(s.name.toLowerCase()) || (pending && s.visibleWhen)) continue;
    const fields = s.fields
      .filter((i) => !hiddenFields.has(itemField(i).toLowerCase()) && !(pending && typeof i !== 'string' && i.visibleWhen))
      .map((i) => ({ field: byName.get(itemField(i).toLowerCase()), wide: itemWide(i) }))
      .filter((x): x is { field: FieldDef; wide: boolean } => !!x.field);
    if (fields.length) out.push({ name: s.name, label: s.label, description: s.description, tab: s.tab, columns: s.columns ?? 2, fields });
  }
  const rest = candidates.filter((f) => !placed.has(f.name.toLowerCase()) && !unplacedElsewhere(f));
  if (rest.length) {
    const lastTab = def.layout.sections[def.layout.sections.length - 1]?.tab;
    out.push({ name: OTHER_SECTION, label: 'Otros datos', tab: lastTab, columns: 2, fields: rest.map((field) => ({ field, wide: false })) });
  }
  return out;
}

/** Campos que el layout menciona (aunque ahora estén ocultos por condición). */
export function placedFields(def: EntityDef): Set<string> {
  return new Set((def.layout?.sections ?? []).flatMap((s) => s.fields.map((i) => itemField(i).toLowerCase())));
}

/** Pestañas en orden de aparición (vacío si el layout no usa pestañas). */
export function formTabs(sections: FormSection[]): string[] {
  return [...new Set(sections.map((s) => s.tab).filter((t): t is string => !!t))];
}

/** Campos que el layout oculta por condición: no se validan ni se exigen (el servidor aplica la misma regla). */
export function hiddenByLayout(hidden: LayoutState | null): Set<string> {
  return new Set((hidden?.hiddenFields ?? []).map((s) => s.toLowerCase()));
}
