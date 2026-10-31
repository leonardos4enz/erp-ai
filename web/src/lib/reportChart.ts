import type { ChartKind, ReportColumn, ReportPeriod, ReportResult } from '../api/types';

/**
 * Spec 039: datos de la gráfica de un reporte, sin DOM. Series (la segunda agrupación o varias medidas), «Otros» más allá de
 * la paleta, relleno de periodos sin registros y comparación del indicador. `ReportChart.tsx` solo dibuja lo que sale de aquí.
 */

/** Paleta del sistema, en el orden de `--chart-1…12` (lista cerrada; la misma que `InterfaceRules.Palette`). */
export const PALETTE = ['azul', 'naranja', 'verde', 'ambar', 'rosa', 'bosque', 'violeta', 'rojo', 'cian', 'oliva', 'purpura', 'lima'] as const;
export const PALETTE_LABELS: Record<(typeof PALETTE)[number], string> = {
  azul: 'Azul', naranja: 'Naranja', verde: 'Verde', ambar: 'Ámbar', rosa: 'Rosa', bosque: 'Verde bosque',
  violeta: 'Violeta', rojo: 'Rojo', cian: 'Cian', oliva: 'Oliva', purpura: 'Púrpura', lima: 'Lima',
};
export const MAX_SERIES = 12;
export const OTHERS = 'Otros';
/** Tope del relleno de periodos: más que eso no se dibuja legible y la tabla ya muestra lo que hay. */
export const MAX_FILL = 1000;

/** Color de la serie <paramref name="i"/>: el declarado si es de la paleta; si no, el lugar fijo (nunca cíclico). */
export function seriesColor(i: number, colors?: string[] | null): string {
  const named = colors?.[i];
  const k = named ? PALETTE.indexOf(named as (typeof PALETTE)[number]) : -1;
  return `var(--chart-${(k >= 0 ? k : i % MAX_SERIES) + 1})`;
}

// ---------------- Periodos ----------------

const DAY = 86_400_000;
/** Date.UTC sin la trampa de los años 0–99 (que toma como 1900–1999). */
function utc(y: number, m: number, d: number): number {
  const t = new Date(Date.UTC(2000, m, d));
  t.setUTCFullYear(y, m, d);
  return t.getTime();
}

/** Inicio (UTC) del periodo de una etiqueta del servidor: 2026, 2026-T1, 2026-05, 2026-S05 (ISO) o 2026-05-04. */
export function parsePeriod(label: string, period: ReportPeriod): number | null {
  let m: RegExpMatchArray | null;
  switch (period) {
    case 'year': return (m = /^(\d{4})$/.exec(label)) ? utc(+m[1], 0, 1) : null;
    case 'quarter': return (m = /^(\d{4})-T([1-4])$/.exec(label)) ? utc(+m[1], (+m[2] - 1) * 3, 1) : null;
    case 'month': return (m = /^(\d{4})-(\d{2})$/.exec(label)) ? utc(+m[1], +m[2] - 1, 1) : null;
    case 'week': {
      if (!(m = /^(\d{4})-S(\d{2})$/.exec(label))) return null;
      const jan4 = utc(+m[1], 0, 4);
      const monday1 = jan4 - ((new Date(jan4).getUTCDay() + 6) % 7) * DAY;
      return monday1 + (+m[2] - 1) * 7 * DAY;
    }
    default: return (m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(label)) ? utc(+m[1], +m[2] - 1, +m[3]) : null;
  }
}

const pad = (n: number) => String(n).padStart(2, '0');

export function formatPeriod(t: number, period: ReportPeriod): string {
  const d = new Date(t);
  const y = d.getUTCFullYear();
  switch (period) {
    case 'year': return String(y).padStart(4, '0');
    case 'quarter': return `${String(y).padStart(4, '0')}-T${Math.floor(d.getUTCMonth() / 3) + 1}`;
    case 'month': return `${String(y).padStart(4, '0')}-${pad(d.getUTCMonth() + 1)}`;
    case 'week': {
      // Semana ISO: la del jueves de esa semana.
      const thursday = new Date(t + (3 - ((d.getUTCDay() + 6) % 7)) * DAY);
      const iy = thursday.getUTCFullYear();
      const week = Math.floor((thursday.getTime() - utc(iy, 0, 1)) / DAY / 7) + 1;
      return `${String(iy).padStart(4, '0')}-S${pad(week)}`;
    }
    default: return `${String(y).padStart(4, '0')}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
  }
}

export function nextPeriod(t: number, period: ReportPeriod, step = 1): number {
  const d = new Date(t);
  switch (period) {
    case 'year': return utc(d.getUTCFullYear() + step, 0, 1);
    case 'quarter': return utc(d.getUTCFullYear(), d.getUTCMonth() + 3 * step, 1);
    case 'month': return utc(d.getUTCFullYear(), d.getUTCMonth() + step, 1);
    case 'week': return t + 7 * step * DAY;
    default: return t + step * DAY;
  }
}

/**
 * H2 CA3: completa los periodos sin registros entre el primero y el último. Solo si las categorías vienen en orden de
 * fecha ascendente (un reporte ordenado por la medida no se rellena) y sin pasar de {@link MAX_FILL}. «(sin valor)» y
 * «(sin acceso)» quedan al final. Devuelve null si no hay nada que rellenar.
 */
export function fillPeriods(categories: string[], period: ReportPeriod, max = MAX_FILL): string[] | null {
  const dated = categories.map((c) => ({ c, t: parsePeriod(c, period) }));
  const times = dated.filter((x) => x.t !== null).map((x) => x.t!) ;
  if (times.length < 2) return null;
  for (let i = 1; i < times.length; i++) if (times[i] <= times[i - 1]) return null;
  const out: string[] = [];
  for (let t = times[0]; t <= times[times.length - 1]; t = nextPeriod(t, period)) {
    out.push(formatPeriod(t, period));
    if (out.length > max) return null;
  }
  if (out.length === times.length) return null;
  return [...out, ...dated.filter((x) => x.t === null).map((x) => x.c)];
}

// ---------------- Series ----------------

export interface Series { label: string; values: (number | null)[]; color: string; line: boolean; column: ReportColumn }
export interface ChartData { kind: ChartKind; group: ReportColumn; categories: string[]; series: Series[]; stacked: boolean; values: boolean; legend: boolean }

const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
/** Lo que vale un periodo sin registros: 0 en conteo y suma, vacío en lo demás (H2 CA3). */
const emptyValue = (c: ReportColumn): number | null => (c.fn === 'count' || c.fn === 'sum' ? 0 : null);

/**
 * Series de la gráfica: con dos agrupaciones, la segunda (una medida); con una, cada medida elegida. <paramref name="measure"/>
 * (tarjeta de tablero) elige una sola medida.
 */
export function buildChart(r: ReportResult, kind: ChartKind, measure?: number): ChartData | null {
  const groups = r.columns.filter((c) => c.kind === 'group');
  const measures = r.columns.filter((c) => c.kind === 'measure');
  const o = r.chartOptions ?? {};
  const group = groups[0];
  if (!group) return null;
  const plotted = (measure !== undefined ? [measure] : o.measures?.length ? o.measures : [0])
    .filter((i) => measures[i]?.type === 'number');
  if (!plotted.length) return null;

  let categories: string[] = [];
  let series: Series[];
  if (groups.length >= 2) {
    const m = measures[plotted[0]];
    const sg = groups[1];
    const cell = new Map<string, number | null>();
    const totals = new Map<string, number>();
    for (const row of r.rows) {
      const x = String(row[group.key] ?? '—');
      const s = String(row[sg.key] ?? '—');
      if (!categories.includes(x)) categories.push(x);
      const v = num(row[m.key]);
      cell.set(`${x}\u0000${s}`, v);
      totals.set(s, (totals.get(s) ?? 0) + Math.abs(v ?? 0));
    }
    // Orden fijo por tamaño; más allá de la paleta, las menores se juntan en «Otros».
    let names = [...totals.keys()].sort((a, b) => totals.get(b)! - totals.get(a)! || a.localeCompare(b, 'es'));
    const folded = names.length > MAX_SERIES ? names.slice(MAX_SERIES - 1) : [];
    if (folded.length) names = [...names.slice(0, MAX_SERIES - 1), OTHERS];
    series = names.map((s, i) => ({
      label: s, line: false, column: m, color: s === OTHERS ? 'var(--chart-other)' : seriesColor(i, o.colors),
      values: categories.map((x) => {
        if (s !== OTHERS) return cell.has(`${x}\u0000${s}`) ? cell.get(`${x}\u0000${s}`)! : emptyValue(m);
        return folded.reduce((a, f) => a + (cell.get(`${x}\u0000${f}`) ?? 0), 0);
      }),
    }));
  } else {
    categories = r.rows.map((row) => String(row[group.key] ?? '—'));
    series = plotted.map((i, k) => ({
      label: measures[i].label, column: measures[i], color: seriesColor(k, o.colors),
      line: kind === 'line' || kind === 'area' ? true : kind === 'combo' && (o.lines ?? []).includes(i),
      values: r.rows.map((row) => num(row[measures[i].key])),
    }));
  }

  // Ejes de fecha: periodos sin registros en su lugar (columnas, barras, línea, área, combinada).
  if (group.period && kind !== 'pie' && kind !== 'donut') {
    const full = fillPeriods(categories, group.period);
    if (full) {
      const at = new Map(categories.map((c, i) => [c, i]));
      series = series.map((s) => ({ ...s, values: full.map((c) => (at.has(c) ? s.values[at.get(c)!] : emptyValue(s.column))) }));
      categories = full;
    }
  }
  const stacked = !!o.stacked && series.length > 1 && (kind === 'column' || kind === 'bar' || kind === 'area');
  return { kind, group, categories, series, stacked, values: !!o.values, legend: o.legend ?? series.length > 1 };
}

// ---------------- Escala ----------------

/** Escala con marcas «redondas» (1, 2, 2.5, 5 × 10ⁿ) que incluye el cero. */
export function niceScale(min: number, max: number, ticks = 4): { min: number; max: number; step: number } {
  const lo = Math.min(0, min);
  const hi = Math.max(0, max);
  if (lo === hi) return { min: 0, max: 1, step: 0.25 };
  const raw = (hi - lo) / ticks;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((f) => f * mag).find((s) => s >= raw)!;
  return { min: Math.floor(lo / step) * step, max: Math.ceil(hi / step) * step, step };
}

/** Extremos de lo que se dibuja: apilado, la suma de positivos y de negativos por categoría. */
export function extent(d: ChartData): [number, number] {
  let lo = 0, hi = 0;
  d.categories.forEach((_, i) => {
    if (d.stacked) {
      let pos = 0, neg = 0;
      for (const s of d.series) { const v = s.values[i] ?? 0; if (v >= 0) pos += v; else neg += v; }
      hi = Math.max(hi, pos); lo = Math.min(lo, neg);
    } else for (const s of d.series) { const v = s.values[i]; if (v !== null) { hi = Math.max(hi, v); lo = Math.min(lo, v); } }
  });
  return [lo, hi];
}

/** Ancho mínimo (px) de la etiqueta de un valor («1.5 k», «900»). */
export const VALUE_LABEL_WIDTH = 30;
export const MAX_VALUE_LABELS = 24;

/**
 * ¿Caben los valores escritos sobre las marcas? Cada categoría (de <paramref name="band"/> px) lleva una etiqueta por marca
 * (columnas y puntos de línea). Si no caben, quedan en el cursor y en la tabla.
 * Regresión (revisión exploratoria 039, F-01): en una tarjeta angosta la combinada con valores encimaba las etiquetas de las
 * columnas y las de la línea; solo se contaba el número de marcas, no el ancho.
 */
export function valueLabelsFit(band: number, marksPerCategory: number, categories: number): boolean {
  const marks = Math.max(1, marksPerCategory);
  return categories * marks <= MAX_VALUE_LABELS && band >= VALUE_LABEL_WIDTH * marks;
}

/**
 * Rebanadas de un pastel o una dona: de mayor a menor (así la primera lleva el primer color, como la serie mayor de las demás
 * gráficas) y, más allá de <paramref name="max"/>, las menores juntas en «Otros».
 * Regresión (revisión exploratoria 039, F-02): no se ordenaban: «Otros» podía juntar rebanadas grandes y una categoría tomaba
 * otro color que en la gráfica apilada del mismo tablero.
 */
export function pieSlices(categories: string[], values: (number | null)[], max: number): { label: string; value: number; other: boolean }[] {
  const all = categories.map((label, i) => ({ label, value: values[i] ?? 0, other: false })).filter((p) => p.value > 0)
    .sort((a, b) => b.value - a.value || a.label.localeCompare(b.label, 'es'));
  if (all.length <= max) return all;
  return [...all.slice(0, max - 1), { label: OTHERS, value: all.slice(max - 1).reduce((a, p) => a + p.value, 0), other: true }];
}

// ---------------- Indicador (H4) ----------------

export interface Kpi { value: number | null; label?: string; previous?: number | null; previousLabel?: string; change: number | null; direction: 'up' | 'down' | 'flat' | null; column: ReportColumn }

/**
 * Valor del indicador: con comparación, el último periodo contra el anterior; sin ella, el total. Con un anterior en cero o
 * vacío no hay porcentaje (H4 CA2): no se inventa un «+∞ %».
 */
export function kpiOf(r: ReportResult, measure = 0): Kpi | null {
  const measures = r.columns.filter((c) => c.kind === 'measure');
  const column = measures[measure] ?? measures[0];
  if (!column) return null;
  const c = r.comparison;
  if (!c) return { value: num(r.totals[column.key]), change: null, direction: null, column };
  const value = num(c.current.values[column.key]);
  const previous = c.previous ? num(c.previous.values[column.key]) : null;
  const change = value !== null && previous !== null && previous !== 0 ? ((value - previous) / Math.abs(previous)) * 100 : null;
  const direction = value === null || previous === null ? null : value > previous ? 'up' : value < previous ? 'down' : 'flat';
  return { value, label: c.current.label, previous, previousLabel: c.previous?.label, change, direction, column };
}
