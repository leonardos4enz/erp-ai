import type { EntityDef, FieldDef, Row } from '../api/types';
import { activeFields, isComputedField, isEditableField } from './entity';
import { toDateInput, toDateTimeInput } from './format';

/** Valores del formulario: tal como los manejan los inputs (strings para números y fechas). */
export type Values = Record<string, unknown>;

export interface LineDraft {
  _key: string;
  id?: number;
  values: Values;
  /** Campos que la persona modificó en esta partida. */
  touched: Record<string, boolean>;
}

export interface Draft {
  values: Values;
  touched: Record<string, boolean>;
  /** Partidas por nombre del campo `lines`. */
  lines: Record<string, LineDraft[]>;
}

let seq = 0;
export const newLineKey = () => `l${++seq}`;

export function isEmptyValue(f: FieldDef, v: unknown): boolean {
  if (f.type === 'boolean') return false;
  return v === null || v === undefined || (typeof v === 'string' && v.trim() === '');
}

/** Valor crudo del servidor → representación del input. */
export function toDraftValue(f: FieldDef, raw: unknown, hasRow: boolean): unknown {
  switch (f.type) {
    case 'boolean': return hasRow ? !!raw : false;
    case 'date': return toDateInput(raw);
    case 'datetime': return toDateTimeInput(raw);
    case 'integer':
    case 'decimal': return raw == null ? '' : String(raw);
    case 'reference':
    case 'user':
    case 'parent': return raw == null || raw === '' ? null : Number(raw);
    case 'formula':
    case 'autonumber':
    case 'state': return raw ?? null;
    default: return raw == null ? '' : String(raw);
  }
}

function hasLabel(f: FieldDef) {
  return f.type === 'reference' || f.type === 'user' || f.type === 'parent';
}

export function rowToValues(fields: FieldDef[], row?: Record<string, unknown>): Values {
  const v: Values = {};
  for (const f of fields) {
    if (f.type === 'lines') continue;
    v[f.name] = toDraftValue(f, row ? row[f.name] : undefined, !!row);
    if (hasLabel(f)) v[`${f.name}__label`] = row?.[`${f.name}__label`] ?? null;
  }
  return v;
}

/** Campos de una partida (sin el campo que apunta al documento). */
export function lineFields(child: EntityDef): FieldDef[] {
  return activeFields(child).filter((f) => f.type !== 'parent' && f.type !== 'lines');
}

export function draftFromRow(def: EntityDef, children: Record<string, EntityDef>, row?: Row): Draft {
  const fields = activeFields(def);
  const lines: Record<string, LineDraft[]> = {};
  for (const lf of fields.filter((f) => f.type === 'lines')) {
    const child = lf.childEntity ? children[lf.childEntity] : undefined;
    const raw = row?.[lf.name];
    const rows = Array.isArray(raw) ? (raw as Record<string, unknown>[]) : [];
    lines[lf.name] = child
      ? rows.map((r) => ({ _key: newLineKey(), id: typeof r.id === 'number' ? r.id : undefined, values: rowToValues(lineFields(child), r), touched: {} }))
      : [];
  }
  return { values: rowToValues(fields, row), touched: {}, lines };
}

export function blankLine(child: EntityDef): LineDraft {
  return { _key: newLineKey(), values: rowToValues(lineFields(child)), touched: {} };
}

/** Input → valor JSON para la API. */
function toApiValue(f: FieldDef, v: unknown): unknown {
  switch (f.type) {
    case 'boolean': return !!v;
    case 'integer': return v === '' || v == null ? null : Number.parseInt(String(v), 10);
    case 'decimal': return v === '' || v == null ? null : Number(String(v));
    case 'datetime': return v ? new Date(String(v)).toISOString() : null;
    case 'reference':
    case 'user': return v ?? null;
    default: return v === '' ? null : v;
  }
}

/**
 * Valores para enviar. En registros nuevos se omiten los campos que la persona no tocó:
 * así el servidor aplica el valor por defecto (que solo aplica cuando el campo viene vacío).
 */
function fieldsPayload(fields: FieldDef[], values: Values, touched: Record<string, boolean>, isNew: boolean): Values {
  const out: Values = {};
  for (const f of fields) {
    if (!isEditableField(f)) continue;
    if (isNew && !touched[f.name]) continue;
    out[f.name] = toApiValue(f, values[f.name]);
  }
  return out;
}

export function buildPayload(def: EntityDef, children: Record<string, EntityDef>, d: Draft, isNew: boolean): Values {
  const fields = activeFields(def);
  const out = fieldsPayload(fields, d.values, d.touched, isNew);
  for (const lf of fields.filter((f) => f.type === 'lines')) {
    const child = lf.childEntity ? children[lf.childEntity] : undefined;
    if (!child) continue;
    const lf2 = lineFields(child);
    out[lf.name] = (d.lines[lf.name] ?? []).map((l) => {
      const row = fieldsPayload(lf2, l.values, l.touched, l.id == null);
      return l.id != null ? { id: l.id, ...row } : row;
    });
  }
  return out;
}

/**
 * Fusiona la respuesta de /compute en el borrador actual:
 * - fórmulas y folios siempre se toman del servidor (fuente de verdad);
 * - los demás campos solo si la persona no los ha tocado o están vacíos (valor sugerido).
 */
function mergeValues(fields: FieldDef[], current: Values, touched: Record<string, boolean>, server: Record<string, unknown>): Values {
  const next = { ...current };
  for (const f of fields) {
    if (f.type === 'lines' || !(f.name in server)) continue;
    const take = isComputedField(f) || !touched[f.name] || isEmptyValue(f, current[f.name]);
    if (!take) continue;
    const incoming = toDraftValue(f, server[f.name], true);
    if (!isComputedField(f) && isEmptyValue(f, incoming) && touched[f.name]) continue;
    next[f.name] = incoming;
    if (hasLabel(f) && `${f.name}__label` in server) next[`${f.name}__label`] = server[`${f.name}__label`] ?? null;
  }
  return next;
}

export function mergeComputed(def: EntityDef, children: Record<string, EntityDef>, d: Draft, server: Record<string, unknown>): Draft {
  const fields = activeFields(def);
  const lines: Record<string, LineDraft[]> = { ...d.lines };
  for (const lf of fields.filter((f) => f.type === 'lines')) {
    const child = lf.childEntity ? children[lf.childEntity] : undefined;
    const srv = server[lf.name];
    if (!child || !Array.isArray(srv)) continue;
    const cf = lineFields(child);
    lines[lf.name] = (d.lines[lf.name] ?? []).map((l, i) => {
      const s = srv[i] as Record<string, unknown> | undefined;
      return s ? { ...l, values: mergeValues(cf, l.values, l.touched, s) } : l;
    });
  }
  return { ...d, values: mergeValues(fields, d.values, d.touched, server), lines };
}

/**
 * Spec 005: resuelve el filtro del buscador de una referencia con los valores actuales del formulario.
 * `source` es un campo del mismo registro o, en una partida, `CampoPadre.Campo` del documento.
 */
export function lookupFilterFor(
  f: FieldDef, own: { def: EntityDef; values: Values }, parent?: { def: EntityDef; values: Values },
): { field: string; value: unknown; sourceLabel: string } | undefined {
  const lf = f.lookupFilter;
  if (!lf?.field || !lf.source) return undefined;
  const [head, tail] = lf.source.split('.');
  const src = tail ? parent : own;
  if (!src) return undefined;
  const name = tail ?? head;
  const sf = src.def.fields.find((x) => x.name.toLowerCase() === name.toLowerCase());
  return { field: lf.field, value: src.values[sf?.name ?? name] ?? null, sourceLabel: sf?.label ?? name };
}

/** Clave de error del servidor para una celda de partida: `Partidas[0].Cantidad`. */
export const lineErrorKey = (linesField: string, index: number, field: string) => `${linesField}[${index}].${field}`;
