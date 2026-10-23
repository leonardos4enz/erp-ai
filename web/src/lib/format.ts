const dateFmt = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium' });
const dateTimeFmt = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeStyle: 'short' });
const intFmt = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 0 });
const decFmt = new Intl.NumberFormat('es-MX', { minimumFractionDigits: 0, maximumFractionDigits: 6 });

/** Parses "YYYY-MM-DD" as a local date (avoids UTC shift), otherwise any ISO string. */
function toDate(v: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(v);
  const d = m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : new Date(v);
  return Number.isNaN(d.getTime()) ? null : d;
}

export function formatDate(v: unknown): string {
  if (v == null || v === '') return '';
  const d = toDate(String(v));
  return d ? dateFmt.format(d) : String(v);
}

export function formatDateTime(v: unknown): string {
  if (v == null || v === '') return '';
  const d = toDate(String(v));
  return d ? dateTimeFmt.format(d) : String(v);
}

export function formatNumber(v: unknown, decimal = false): string {
  if (v == null || v === '') return '';
  const n = typeof v === 'number' ? v : Number(v);
  if (Number.isNaN(n)) return String(v);
  return (decimal ? decFmt : intFmt).format(n);
}

/** Value for <input type="date"> */
export function toDateInput(v: unknown): string {
  if (v == null || v === '') return '';
  const s = String(v);
  return /^\d{4}-\d{2}-\d{2}/.test(s) ? s.slice(0, 10) : '';
}

/** Value for <input type="datetime-local"> (local time, minutes precision) */
export function toDateTimeInput(v: unknown): string {
  if (v == null || v === '') return '';
  const d = new Date(String(v));
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
}

/** "Tipo de empaque" → "TipoDeEmpaque" — ASCII identifier matching ^[A-Za-z][A-Za-z0-9_]{1,62}$ */
export function toIdentifier(label: string): string {
  const ascii = label.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/ñ/gi, 'n');
  const words = ascii.replace(/[^A-Za-z0-9]+/g, ' ').trim().split(/\s+/).filter(Boolean);
  let id = words.map((w) => w.charAt(0).toUpperCase() + w.slice(1)).join('');
  id = id.replace(/^[0-9]+/, '');
  return id.slice(0, 50);
}

/** Plural sugerido en español: almacén→almacenes, lápiz→lápices, caja→cajas, proveedor→proveedores. */
export function pluralize(word: string): string {
  const w = word.trim();
  if (!w) return '';
  if (/[aeiouáéó]$/i.test(w)) return `${w}s`;
  if (/z$/i.test(w)) return `${w.slice(0, -1)}ces`;
  if (/(is|us|x)$/.test(w)) return w; // llanas invariables: análisis, virus, tórax
  if (/[íú]$/i.test(w)) return `${w}es`;
  // Agudas en -án, -én, -ín, -ón, -ún, -és…: el acento desaparece al agregar sílaba.
  const unaccent: Record<string, string> = { á: 'a', é: 'e', í: 'i', ó: 'o', ú: 'u', Á: 'A', É: 'E', Í: 'I', Ó: 'O', Ú: 'U' };
  const m = /([áéíóúÁÉÍÓÚ])([ns])$/.exec(w);
  if (m) return `${w.slice(0, -2)}${unaccent[m[1]]}${m[2]}es`;
  return `${w}es`;
}

// Debe coincidir con Identifiers.cs en el backend: 2-50 caracteres y sin "__" (reservado).
export const IDENTIFIER_RE = /^(?!.*__)[A-Za-z][A-Za-z0-9_]{1,49}$/;
