import type { FieldDef } from '../api/types';
import { fieldsAtPath, type CompletionScope } from './exprComplete';

/**
 * Spec 026 (H3 CA5, pedido del dueño): constructor de condiciones. Renglones «campo · operador · valor» unidos por Y u O
 * que se convierten en la misma expresión de texto que se escribiría a mano (y que valida el mismo analizador). Al revés,
 * una condición escrita se lee en renglones solo si tiene esa forma simple; si no, se edita como texto.
 */

export type CondOp = 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'blank' | 'notBlank' | 'in';
export type CondValue =
  | { kind: 'text'; value: string }
  | { kind: 'number'; value: string }
  | { kind: 'bool'; value: boolean }
  | { kind: 'field'; value: string }
  | { kind: 'today'; days: number }
  | { kind: 'me' }
  | { kind: 'list'; values: string[] };
export interface Cond { path: string; op: CondOp; value?: CondValue }
export interface CondGroup { join: 'AND' | 'OR'; items: Cond[] }

export type FieldKind = 'text' | 'number' | 'date' | 'boolean' | 'choice' | 'user' | 'reference';

export const OP_TEXT: Record<CondOp, string> = {
  eq: 'es igual a', ne: 'es distinto de', gt: 'es mayor que', gte: 'es mayor o igual a', lt: 'es menor que', lte: 'es menor o igual a',
  blank: 'está vacío', notBlank: 'no está vacío', in: 'es uno de',
};
const SYMBOL: Partial<Record<CondOp, string>> = { eq: '=', ne: '<>', gt: '>', gte: '>=', lt: '<', lte: '<=' };
const FROM_SYMBOL: Record<string, CondOp> = { '=': 'eq', '<>': 'ne', '>': 'gt', '>=': 'gte', '<': 'lt', '<=': 'lte' };

export function kindOfField(f?: FieldDef): FieldKind {
  if (!f) return 'text';
  const t = f.type === 'formula' ? f.resultType ?? 'decimal' : f.type;
  if (t === 'choice' || t === 'state') return 'choice';
  if (t === 'integer' || t === 'decimal') return 'number';
  if (t === 'date' || t === 'datetime') return 'date';
  if (t === 'boolean') return 'boolean';
  if (t === 'user') return 'user';
  if (t === 'reference' || t === 'parent') return 'reference';
  return 'text';
}

/** Operadores que tienen sentido para cada clase de campo. */
export const OPS_FOR: Record<FieldKind, CondOp[]> = {
  text: ['eq', 'ne', 'blank', 'notBlank'],
  number: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'blank', 'notBlank'],
  date: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'blank', 'notBlank'],
  boolean: ['eq'],
  choice: ['eq', 'ne', 'in', 'blank', 'notBlank'],
  user: ['eq', 'ne', 'blank', 'notBlank'],
  reference: ['blank', 'notBlank', 'eq', 'ne'],
};

/** Campo al que lleva una ruta (`Cliente.Zona.Clave`), o undefined. */
export function fieldAt(scope: CompletionScope, path: string): FieldDef | undefined {
  const segs = path.split('.');
  const last = segs.pop()!;
  const at = segs.length ? fieldsAtPath(scope, segs) : { fields: scope.fields, user: false, lines: false };
  return at?.fields.find((f) => !f.isArchived && f.name.toLowerCase() === last.toLowerCase());
}

/** Valor inicial razonable al elegir un campo u operador. */
export function defaultValue(kind: FieldKind, f?: FieldDef, states?: string[]): CondValue | undefined {
  switch (kind) {
    case 'number': return { kind: 'number', value: '0' };
    case 'date': return { kind: 'today', days: 0 };
    case 'boolean': return { kind: 'bool', value: true };
    case 'choice': return { kind: 'text', value: (f?.type === 'state' ? states?.[0] : f?.options?.[0]) ?? '' };
    case 'user': return { kind: 'me' };
    case 'reference': return { kind: 'field', value: '' };
    default: return { kind: 'text', value: '' };
  }
}

const quote = (s: string) => `'${s.replace(/'/g, "''")}'`;

function valueText(v: CondValue | undefined): string {
  if (!v) return "''";
  switch (v.kind) {
    case 'text': return quote(v.value);
    case 'number': return v.value.trim() || '0';
    case 'bool': return v.value ? 'TRUE' : 'FALSE';
    case 'field': return v.value || "''";
    case 'today': return v.days ? `ADDDAYS(TODAY(), ${v.days})` : 'TODAY()';
    case 'me': return 'CURRENTUSER()';
    case 'list': return `(${v.values.map(quote).join(', ')})`;
  }
}

export function condText(c: Cond): string {
  if (!c.path) return '';
  if (c.op === 'blank') return `ISBLANK(${c.path})`;
  if (c.op === 'notBlank') return `NOT ISBLANK(${c.path})`;
  if (c.op === 'in') return `${c.path} IN ${valueText(c.value?.kind === 'list' ? c.value : { kind: 'list', values: [] })}`;
  return `${c.path} ${SYMBOL[c.op]} ${valueText(c.value)}`;
}

/** La expresión que equivale a los renglones (los incompletos se omiten). */
export function buildCondition(g: CondGroup): string {
  return g.items.map(condText).filter(Boolean).join(` ${g.join} `);
}

/* ------------------------------------------------------------------------- */
/* Leer una condición escrita                                                 */

/** Parte por AND/OR fuera de cadenas y paréntesis; null si mezcla AND con OR. */
function splitTop(text: string): { join: 'AND' | 'OR'; parts: string[] } | null {
  const parts: string[] = [];
  let depth = 0, inStr = false, start = 0;
  let join: 'AND' | 'OR' | null = null;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === "'") { if (inStr && text[i + 1] === "'") { i++; continue; } inStr = !inStr; continue; }
    if (inStr) continue;
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    else if (depth === 0) {
      const m = /^\s+(AND|OR)\s+/i.exec(text.slice(i));
      if (m) {
        const j = m[1].toUpperCase() as 'AND' | 'OR';
        if (join && join !== j) return null;
        join = j;
        parts.push(text.slice(start, i));
        i += m[0].length - 1;
        start = i + 1;
      }
    }
  }
  if (inStr || depth !== 0) return null;
  parts.push(text.slice(start));
  return { join: join ?? 'AND', parts: parts.map((p) => p.trim()) };
}

const PATH = '([A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*){0,3})';
const STR = "'((?:[^']|'')*)'";
const unquote = (s: string) => s.replace(/''/g, "'");

function parseValue(raw: string): CondValue | null {
  const s = raw.trim();
  let m: RegExpExecArray | null;
  if ((m = new RegExp(`^${STR}$`).exec(s))) return { kind: 'text', value: unquote(m[1]) };
  if (/^-?\d+(\.\d+)?$/.test(s)) return { kind: 'number', value: s };
  if (/^TRUE$/i.test(s)) return { kind: 'bool', value: true };
  if (/^FALSE$/i.test(s)) return { kind: 'bool', value: false };
  if (/^TODAY\(\)$/i.test(s)) return { kind: 'today', days: 0 };
  if ((m = /^ADDDAYS\(\s*TODAY\(\)\s*,\s*(-?\d+)\s*\)$/i.exec(s))) return { kind: 'today', days: Number(m[1]) };
  if (/^CURRENTUSER\(\)$/i.test(s)) return { kind: 'me' };
  if (new RegExp(`^${PATH}$`).test(s) && !/^(AND|OR|NOT|IN|NULL)$/i.test(s)) return { kind: 'field', value: s };
  return null;
}

function parseCond(part: string, scope: CompletionScope): Cond | null {
  let m: RegExpExecArray | null;
  if ((m = new RegExp(`^NOT\\s+ISBLANK\\(\\s*${PATH}\\s*\\)$`, 'i').exec(part))) return known({ path: m[1], op: 'notBlank' }, scope);
  if ((m = new RegExp(`^ISBLANK\\(\\s*${PATH}\\s*\\)$`, 'i').exec(part))) return known({ path: m[1], op: 'blank' }, scope);
  if ((m = new RegExp(`^${PATH}\\s+IN\\s*\\((.*)\\)$`, 'i').exec(part))) {
    const items = m[2].split(',').map((x) => x.trim());
    const values: string[] = [];
    for (const it of items) { const v = new RegExp(`^${STR}$`).exec(it); if (!v) return null; values.push(unquote(v[1])); }
    return known({ path: m[1], op: 'in', value: { kind: 'list', values } }, scope);
  }
  if ((m = new RegExp(`^${PATH}\\s*(<>|<=|>=|=|<|>)\\s*(.+)$`).exec(part))) {
    const value = parseValue(m[3]);
    return value ? known({ path: m[1], op: FROM_SYMBOL[m[2]], value }, scope) : null;
  }
  return null;
}

/** Solo se leen renglones de campos que existen y operadores que el constructor ofrece para su tipo. */
function known(c: Cond, scope: CompletionScope): Cond | null {
  const f = fieldAt(scope, c.path);
  if (!f) return null;
  return OPS_FOR[kindOfField(f)].includes(c.op) ? c : null;
}

/** Renglones equivalentes a la condición, o null si no tiene la forma simple que el constructor representa. */
export function parseCondition(text: string, scope: CompletionScope): CondGroup | null {
  if (!text.trim()) return { join: 'AND', items: [] };
  const top = splitTop(text.trim());
  if (!top) return null;
  const items: Cond[] = [];
  for (const p of top.parts) {
    const c = parseCond(p, scope);
    if (!c) return null;
    items.push(c);
  }
  return { join: top.join, items };
}
