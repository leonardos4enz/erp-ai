import type { EntityDef, ExprFunction, FieldDef } from '../api/types';
import { FIELD_TYPE_LABELS } from './entity';

/**
 * Spec 026 (H3): autocompletado del editor de expresiones. Puro (sin React) para probarlo y razonarlo: dado el texto y el
 * cursor, qué palabra se está escribiendo y qué se puede poner ahí (campos, rutas por referencias, funciones u opciones).
 */

export interface Suggestion {
  kind: 'field' | 'function' | 'option' | 'keyword';
  /** Lo que se escribe en lugar de la palabra. */
  insert: string;
  label: string;
  detail?: string;
}

export interface CompletionScope {
  /** Campos del ámbito (los del borrador si es el catálogo que se edita). */
  fields: FieldDef[];
  /** Estados del proceso del ámbito (para `Estado = '…'`). */
  states?: string[];
  /** Modelo completo, para seguir referencias. */
  entities: EntityDef[];
  functions: ExprFunction[];
}

export interface Completion { from: number; to: number; items: Suggestion[]; closeQuote: boolean }

/** Spec 023: hasta tres saltos después del campo inicial (`Recepcion.OrdenCompra.Moneda.Clave`). */
export const MAX_HOPS = 3;
const MAX_ITEMS = 12;
const USER_FIELDS = ['Nombre', 'Usuario'];
const KEYWORDS = ['AND', 'OR', 'NOT', 'IN', 'TRUE', 'FALSE'];

const FUNCTION_HINTS: Record<string, string> = {
  IF: 'IF(condición, si sí, si no)', COALESCE: 'COALESCE(valor, respaldo…)', ISBLANK: 'ISBLANK(valor)', ROUND: 'ROUND(número, decimales)',
  ABS: 'ABS(número)', MIN: 'MIN(a, b)', MAX: 'MAX(a, b)', LEN: 'LEN(texto)', UPPER: 'UPPER(texto)', ISEMAIL: 'ISEMAIL(texto)',
  TODAY: 'TODAY()', NOW: 'NOW()', ADDDAYS: 'ADDDAYS(fecha, días)', DAYS: 'DAYS(desde, hasta)', CURRENTUSER: 'CURRENTUSER()',
  CURRENTUNIT: 'CURRENTUNIT()', HASROLE: "HASROLE('Rol')", ISCREATOR: 'ISCREATOR()', SUM: 'SUM(Partidas.Campo)', COUNT: 'COUNT(Partidas)',
  BASECURRENCY: 'BASECURRENCY()', CURRENTCOMPANY: 'CURRENTCOMPANY()', RATE: 'RATE(de, a, fecha?)', CONVERT: 'CONVERT(importe, de, a, fecha?)',
  UNITCOST: "UNITCOST('Catálogo', cantidad, llave…)",
};

const find = (fields: FieldDef[], name: string) => fields.find((f) => !f.isArchived && f.name.toLowerCase() === name.toLowerCase());
const entityOf = (entities: EntityDef[], name?: string) => entities.find((e) => e.name.toLowerCase() === (name ?? '').toLowerCase());

/** Posiciones donde el cursor está dentro de una cadena `'…'` (con `''` como comilla escapada): índice de la comilla de apertura o -1. */
function openQuote(text: string, caret: number): number {
  let open = -1;
  for (let i = 0; i < caret; i++) {
    if (text[i] !== "'") continue;
    if (open < 0) open = i;
    else if (text[i + 1] === "'" && i + 1 < caret) i++;
    else open = -1;
  }
  return open;
}

/** Campos a los que lleva una ruta (`A.B.` → campos del catálogo de B), o null si no se puede seguir. */
export function fieldsAtPath(scope: CompletionScope, segments: string[]): { fields: FieldDef[]; user: boolean; lines: boolean } | null {
  let fields = scope.fields;
  let lines = false;
  for (let i = 0; i < segments.length; i++) {
    const f = find(fields, segments[i]);
    if (!f) return null;
    if (f.type === 'user') return i === segments.length - 1 ? { fields: [], user: true, lines: false } : null;
    if (f.type === 'lines') {
      // Solo dentro de SUM(...): un nivel, al campo de la partida.
      if (i !== 0 || segments.length !== 1) return null;
      const child = entityOf(scope.entities, f.childEntity);
      return child ? { fields: child.fields, user: false, lines: true } : null;
    }
    if (f.type !== 'reference' && f.type !== 'parent') return null;
    const target = entityOf(scope.entities, f.referenceEntity);
    if (!target) return null;
    fields = target.fields;
  }
  return { fields, user: false, lines };
}

function fieldItem(f: FieldDef, prefix: string): Suggestion {
  const hop = f.type === 'reference' || f.type === 'parent';
  return { kind: 'field', insert: prefix + f.name, label: f.label || f.name, detail: `${f.name} · ${FIELD_TYPE_LABELS[f.type] ?? f.type}${hop ? ' ›' : ''}` };
}

function matches(word: string, ...candidates: (string | undefined)[]) {
  const w = word.toLowerCase();
  return !w || candidates.some((c) => c && c.toLowerCase().includes(w));
}

/** Qué se puede escribir en el cursor. `force` (Ctrl+Espacio) sugiere aunque no haya palabra empezada. */
export function complete(text: string, caret: number, scope: CompletionScope, force = false): Completion | null {
  const quote = openQuote(text, caret);
  if (quote >= 0) {
    // Dentro de una cadena: opciones del campo comparado (`Campo = '`, `<> '`, `IN ('A', '`).
    const before = text.slice(0, quote);
    const m = /([A-Za-z_][A-Za-z0-9_.]*)\s*(?:=|<>|IN\s*\((?:\s*'(?:[^']|'')*'\s*,)*)\s*$/i.exec(before);
    if (!m) return null;
    const segs = m[1].split('.');
    const last = segs.pop()!;
    const at = segs.length ? fieldsAtPath(scope, segs) : { fields: scope.fields, user: false, lines: false };
    const f = at ? find(at.fields, last) : undefined;
    const values = f?.type === 'choice' ? f.options ?? [] : f?.type === 'state' && !segs.length ? scope.states ?? [] : [];
    const typed = text.slice(quote + 1, caret);
    const items = values.filter((v) => matches(typed, v)).slice(0, MAX_ITEMS)
      .map((v): Suggestion => ({ kind: 'option', insert: v.replace(/'/g, "''"), label: v, detail: `Opción de ${f!.label || f!.name}` }));
    return items.length ? { from: quote + 1, to: caret, items, closeQuote: text[caret] !== "'" } : null;
  }

  const m = /[A-Za-z_][A-Za-z0-9_.]*$/.exec(text.slice(0, caret));
  const word = m ? m[0] : '';
  let to = caret;
  while (to < text.length && /[A-Za-z0-9_]/.test(text[to])) to++;
  const from = caret - word.length;
  if (!word && !force) return null;

  if (word.includes('.')) {
    const segs = word.split('.');
    const last = segs.pop()!;
    if (segs.length > MAX_HOPS || segs.some((s) => !s)) return null;
    const at = fieldsAtPath(scope, segs);
    if (!at) return null;
    const prefix = segs.join('.') + '.';
    const items = at.user
      ? USER_FIELDS.filter((n) => matches(last, n)).map((n): Suggestion => ({ kind: 'field', insert: prefix + n, label: n, detail: 'Dato del usuario' }))
      : at.fields.filter((f) => !f.isArchived && f.type !== 'lines' && matches(last, f.name, f.label)).map((f) => fieldItem(f, prefix));
    return items.length ? { from, to, items: items.slice(0, MAX_ITEMS), closeQuote: false } : null;
  }

  const fields = scope.fields.filter((f) => !f.isArchived && matches(word, f.name, f.label)).map((f) => fieldItem(f, ''));
  const upper = word.toUpperCase();
  const fns = scope.functions.filter((f) => f.name.startsWith(upper))
    .map((f): Suggestion => ({ kind: 'function', insert: `${f.name}(${f.max === 0 ? ')' : ''}`, label: f.name, detail: FUNCTION_HINTS[f.name] ?? `${f.name}()` }));
  const kws = word ? KEYWORDS.filter((k) => k.startsWith(upper) && k !== upper).map((k): Suggestion => ({ kind: 'keyword', insert: k, label: k, detail: 'Palabra clave' })) : [];
  const items = [...fields, ...fns, ...kws].slice(0, MAX_ITEMS);
  // Una palabra ya completa y exacta no necesita lista.
  if (items.length === 1 && items[0].insert === word) return null;
  return items.length ? { from, to, items, closeQuote: false } : null;
}

/** Aplica una sugerencia: devuelve el texto nuevo y dónde queda el cursor. */
export function apply(text: string, c: Completion, s: Suggestion): { text: string; caret: number } {
  const insert = s.insert + (s.kind === 'option' && c.closeQuote ? "'" : '');
  const next = text.slice(0, c.from) + insert + text.slice(c.to);
  return { text: next, caret: c.from + insert.length };
}

/**
 * Tras elegir una referencia («Cliente ›») o abrir una función, la lista se vuelve a abrir. Regresión (revisión 026, F-06):
 * se calcula con el texto y el cursor **actuales** del control, no con los del momento de elegir: si la persona ya siguió
 * escribiendo («Cliente.Zona.»), la lista mostraba sugerencias viejas.
 */
export function completeAfterChoice(s: Suggestion, current: { text: string; caret: number }, scope: CompletionScope): Completion | null {
  if (s.kind !== 'function' && !(s.detail ?? '').endsWith('›')) return null;
  return complete(current.text, current.caret, scope, s.kind === 'function');
}
