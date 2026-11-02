import type { EntityDef, FieldDef, LookupCondition, LookupFilter, LookupOp } from '../api/types';

/**
 * Spec 052: filtro compuesto del buscador de una referencia. Lógica sin DOM: las condiciones efectivas, qué falta capturar para
 * que el buscador ofrezca algo, y lo que necesita el editor del modelador (operadores por tipo, rutas de origen compatibles,
 * renglones ↔ JSON). Las reglas son las del servidor (`LookupFilters.cs`); el servidor decide.
 */

/** Condiciones efectivas: la forma corta de la 005 (`field` = `source`) primero. */
export function conditionsOf(lf?: LookupFilter | null): LookupCondition[] {
  if (!lf) return [];
  const list: LookupCondition[] = [];
  if (lf.field) list.push({ field: lf.field, op: 'eq', source: lf.source });
  return [...list, ...(lf.conditions ?? [])];
}

export const hasLookupFilter = (lf?: LookupFilter | null) => conditionsOf(lf).length > 0;

const findField = (def: EntityDef, name: string) => def.fields.find((x) => x.name.toLowerCase() === name.toLowerCase());
const isEmpty = (v: unknown) => v == null || v === '' || (Array.isArray(v) && v.length === 0);

/**
 * Lo que el formulario sabe sin preguntar al servidor: el primer origen vacío (para «Elige primero …») y una firma de los valores
 * de los que depende el filtro (el buscador vuelve a pedir cuando cambia). El primer salto de un origen es un campo propio o, en
 * una partida, el campo padre, que lleva al documento.
 */
export function lookupFilterFor(
  f: FieldDef, own: { def: EntityDef; values: Record<string, unknown> }, parent?: { def: EntityDef; values: Record<string, unknown> },
): { waiting?: string; key: string } | undefined {
  const conds = conditionsOf(f.lookupFilter);
  if (conds.length === 0) return undefined;
  let waiting: string | undefined;
  const parts: unknown[] = [];
  for (const c of conds) {
    if (!c.source) continue;
    const [head, next] = c.source.split('.');
    const hf = findField(own.def, head);
    const viaParent = hf?.type === 'parent' && !!next && !!parent;
    const src = viaParent ? parent! : own;
    const sf = findField(src.def, viaParent ? next : head);
    const value = src.values[sf?.name ?? (viaParent ? next : head)];
    parts.push(value ?? null);
    if (!waiting && isEmpty(value)) waiting = sf?.label ?? head;
  }
  return { waiting, key: JSON.stringify(parts) };
}

/* ----------------------------- Editor del modelador ----------------------------- */

type Kind = 'text' | 'longtext' | 'number' | 'date' | 'boolean' | 'id' | 'multi';

/** Clase de valor como `InterfaceRules.KindOf` del servidor. */
export function kindOf(f: FieldDef): Kind {
  const t = f.type === 'formula' ? f.resultType ?? 'decimal' : f.type;
  switch (t) {
    case 'longtext': return 'longtext';
    case 'integer': case 'decimal': return 'number';
    case 'date': case 'datetime': return 'date';
    case 'boolean': return 'boolean';
    case 'reference': case 'parent': case 'user': return 'id';
    case 'multireference': case 'multiuser': return 'multi';
    default: return 'text';
  }
}

const OPS: Record<Kind, LookupOp[]> = {
  text: ['eq', 'ne', 'contains', 'in', 'blank', 'notBlank'],
  longtext: ['contains', 'blank', 'notBlank'],
  number: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'in', 'blank', 'notBlank'],
  date: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'blank', 'notBlank'],
  boolean: ['eq', 'ne', 'blank', 'notBlank'],
  id: ['eq', 'ne', 'in', 'blank', 'notBlank'],
  multi: ['contains', 'in', 'blank', 'notBlank'],
};

export const LOOKUP_OP_TEXT: Record<LookupOp, string> = {
  eq: 'es igual a', ne: 'es distinto de', gt: 'es mayor que', gte: 'es mayor o igual a', lt: 'es menor que', lte: 'es menor o igual a',
  contains: 'contiene', in: 'es uno de', blank: 'está vacío', notBlank: 'tiene valor',
};

/** Operadores que admite el campo del destino (texto distinto para «es uno de» en varios valores: «contiene alguno de»). */
export function opsFor(f: FieldDef): LookupOp[] {
  return OPS[kindOf(f)];
}

export function opLabel(f: FieldDef | undefined, op: LookupOp): string {
  return f && kindOf(f) === 'multi' && op === 'in' ? 'contiene alguno de' : LOOKUP_OP_TEXT[op];
}

export const needsValue = (op: LookupOp) => op !== 'blank' && op !== 'notBlank';

/** Campos que el filtro puede usar en el catálogo destino: activos, con columna o de varios valores. */
export function targetFields(target: EntityDef): FieldDef[] {
  return target.fields.filter((f) => !f.isArchived && f.type !== 'lines');
}

const domain = (f: FieldDef) =>
  f.type === 'user' || f.type === 'multiuser' ? '@user' : f.type === 'reference' || f.type === 'parent' || f.type === 'multireference' ? (f.referenceEntity ?? '').toLowerCase() : null;
const isDateTime = (f: FieldDef) => (f.type === 'formula' ? f.resultType : f.type) === 'datetime';
const plain = (k: Kind): Kind => (k === 'longtext' ? 'text' : k);

/** ¿El origen se compara con el campo del destino con ese operador? Las mismas reglas que el servidor. */
export function compatible(target: FieldDef, op: LookupOp, source: FieldDef): boolean {
  const tk = plain(kindOf(target));
  const sk = plain(kindOf(source));
  const same = domain(target) != null && domain(target) === domain(source);
  switch (op) {
    case 'eq': case 'ne': case 'gt': case 'gte': case 'lt': case 'lte':
      return tk !== 'multi' && tk === sk && (tk !== 'id' || same) && (tk !== 'date' || isDateTime(target) === isDateTime(source));
    case 'contains':
      return tk === 'multi' ? sk === 'id' && same : tk === 'text' && sk === 'text';
    case 'in':
      return sk === 'multi' && same && (tk === 'multi' || tk === 'id');
    default:
      return false;
  }
}

/**
 * Rutas de origen (hasta tres segmentos) desde el registro que se captura que se pueden comparar con `target` con `op`. Cada
 * salto intermedio es una referencia o el documento padre.
 */
export function sourcePaths(ownerFields: FieldDef[], entities: EntityDef[], target: FieldDef, op: LookupOp): { path: string; label: string }[] {
  const byName = new Map(entities.map((e) => [e.name.toLowerCase(), e]));
  const out: { path: string; label: string }[] = [];
  const walk = (fields: FieldDef[], path: string[], labels: string[]) => {
    for (const f of fields) {
      if (f.isArchived || f.type === 'lines') continue;
      const p = [...path, f.name];
      const l = [...labels, f.label];
      if (compatible(target, op, f)) out.push({ path: p.join('.'), label: l.join(' › ') });
      if (p.length < 3 && (f.type === 'reference' || f.type === 'parent')) {
        const next = byName.get((f.referenceEntity ?? '').toLowerCase());
        if (next) walk(next.fields, p, l);
      }
    }
  };
  walk(ownerFields, [], []);
  return out;
}

/** Renglón del editor: un valor fijo, un origen («del registro») o nada (vacío / con valor). */
export interface LookupRow { field: string; op: LookupOp; mode: 'value' | 'source'; value?: unknown; source?: string }

export function toRows(lf?: LookupFilter | null): LookupRow[] {
  return conditionsOf(lf).map((c) => ({
    field: c.field, op: c.op, mode: c.source != null && c.source !== '' ? 'source' : 'value', value: c.value, source: c.source,
  }));
}

/** El JSON que viaja en el plan; sin renglones, `{ conditions: [] }` quita el filtro. */
export function fromRows(rows: LookupRow[]): LookupFilter {
  return {
    conditions: rows.filter((r) => r.field).map((r): LookupCondition => {
      if (!needsValue(r.op)) return { field: r.field, op: r.op };
      return r.mode === 'source' ? { field: r.field, op: r.op, source: r.source ?? '' } : { field: r.field, op: r.op, value: r.value ?? null };
    }),
  };
}

/** Forma comparable (para saber si el borrador cambió el filtro guardado). */
export const lookupSignature = (lf?: LookupFilter | null) => JSON.stringify(fromRows(toRows(lf)).conditions);
