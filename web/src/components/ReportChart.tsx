import { useLayoutEffect, useRef, useState } from 'react';
import { ArrowDownRight, ArrowUpRight, Minus } from 'lucide-react';
import type { ChartKind, ReportColumn, ReportResult } from '../api/types';
import { buildChart, extent, kpiOf, niceScale, pieSlices, seriesColor, valueLabelsFit, type ChartData, type Series } from '../lib/reportChart';

/** Formato de un valor de reporte según su columna (números con la escala declarada). */
export function formatReportValue(c: ReportColumn, v: unknown): string {
  if (v === null || v === undefined || v === '') return '—';
  if (c.type === 'number' && typeof v === 'number') {
    const s = c.scale ?? 2;
    return v.toLocaleString('es-MX', { minimumFractionDigits: s, maximumFractionDigits: s });
  }
  return String(v);
}

const compact = new Intl.NumberFormat('es-MX', { notation: 'compact', maximumFractionDigits: 1 });
const MAX_SLICES = 8; // un pastel con más rebanadas no se lee; el resto va en «Otros»
const MAX_BARS = 15;

const clip = (s: string, n: number) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

/** Resumen para lector de pantalla: qué se mide, por qué, y los primeros puntos (la tabla de abajo trae todo). */
function summary(d: ChartData): string {
  const what = d.series.length === 1 ? d.series[0].label : d.series.map((s) => s.label).slice(0, 4).join(', ') + (d.series.length > 4 ? '…' : '');
  const first = d.series[0];
  const pts = d.categories.slice(0, 5).map((c, i) => `${c} ${formatReportValue(first.column, first.values[i])}`).join('; ');
  return `${what} por ${d.group.label.toLowerCase()}${d.series.length > 1 ? ` (${d.series.length} series)` : ''}: ${pts}${d.categories.length > 5 ? '…' : ''}`;
}

/** Lo que dice el cursor sobre una categoría: el valor de cada serie. */
function tip(d: ChartData, i: number): string {
  return [d.categories[i], ...d.series.map((s) => `${s.label}: ${formatReportValue(s.column, s.values[i])}`)].join('\n');
}

function Legend({ series }: { series: Series[] }) {
  return (
    <ul className="chart-legend" aria-label="Leyenda">
      {series.map((s) => <li key={s.label}><span className={`chart-swatch${s.line ? ' line' : ''}`} style={{ background: s.color }} aria-hidden />{s.label}</li>)}
    </ul>
  );
}

/**
 * Gráfica de un reporte (spec 007; tipos, series, colores e indicador de la 039) en SVG propio. Un solo eje: la combinada
 * dibuja columnas y líneas sobre la misma escala. Cada categoría tiene su valor en un <title> (se ve al pasar el cursor) y el
 * conjunto un resumen para lectores de pantalla; la tabla va debajo y es el camino para teclado.
 */
export function ReportChart({ result, kind, measure }: { result: ReportResult; kind: ChartKind; measure?: number }) {
  if (kind === 'kpi') return <KpiView result={result} measure={measure} />;
  const d = buildChart(result, kind, measure);
  if (!d || d.categories.length === 0) return null;
  return <Sized d={d} kind={kind} colors={result.chartOptions?.colors} />;
}

/**
 * La gráfica mide lo que su contenedor: una unidad del SVG es un píxel, así el texto mide lo mismo en una tarjeta angosta y
 * en una ancha (revisión 039: con un viewBox fijo, el texto crecía al doble en una tarjeta ancha y no se leía en una angosta).
 */
function Sized({ d, kind, colors }: { d: ChartData; kind: ChartKind; colors?: string[] | null }) {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(560);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setWidth(Math.max(240, Math.round(el.clientWidth)));
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const chart = kind === 'pie' || kind === 'donut' ? <Pie d={d} donut={kind === 'donut'} colors={colors} />
    : kind === 'bar' ? <Bars d={d} W={width} /> : <Cartesian d={d} W={width} />;
  return (
    <div className="chart-wrap" ref={ref}>
      {chart}
      {d.legend && kind !== 'pie' && kind !== 'donut' && <Legend series={d.series} />}
    </div>
  );
}

// ---------------- Columnas, línea, área y combinada ----------------

function Cartesian({ d, W }: { d: ChartData; W: number }) {
  const H = 240, L = 52, R = 8, T = 14, B = 26;
  const plotW = W - L - R, plotH = H - T - B;
  const [lo, hi] = extent(d);
  const sc = niceScale(lo, hi);
  const y = (v: number) => T + plotH * (1 - (v - sc.min) / (sc.max - sc.min));
  const n = d.categories.length;
  const band = plotW / n;
  const cx = (i: number) => L + band * i + band / 2;
  const bars = d.series.filter((s) => !s.line);
  const lines = d.series.filter((s) => s.line);
  const inner = Math.min(band * 0.72, 64);
  const bw = d.stacked ? inner : inner / Math.max(1, bars.length);
  const labelStep = Math.max(1, Math.ceil(n / Math.floor(plotW / 64)));
  const showValues = d.values && valueLabelsFit(band, d.stacked ? 1 : bars.length + lines.length, n);
  const ticks: number[] = [];
  for (let t = sc.min; t <= sc.max + sc.step / 2; t += sc.step) ticks.push(+t.toFixed(10));
  const area = d.kind === 'area';

  // Base de cada serie apilada (positivos hacia arriba, negativos hacia abajo).
  const pos = new Array(n).fill(0), neg = new Array(n).fill(0);
  const stackedBase = d.series.map((s) => s.values.map((v, i) => {
    const val = v ?? 0;
    const base = val >= 0 ? pos[i] : neg[i];
    if (val >= 0) pos[i] += val; else neg[i] += val;
    return base;
  }));

  const segments = (s: Series) => {
    const out: string[][] = [];
    let cur: string[] = [];
    s.values.forEach((v, i) => {
      if (v === null && !d.stacked) { if (cur.length) out.push(cur); cur = []; return; }
      const top = d.stacked ? stackedBase[d.series.indexOf(s)][i] + (v ?? 0) : v!;
      cur.push(`${cx(i).toFixed(1)},${y(top).toFixed(1)}`);
    });
    if (cur.length) out.push(cur);
    return out;
  };

  return (
    <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary(d)}>
      {ticks.map((t) => (
        <g key={t}>
          <line className={t === 0 ? 'chart-axis' : 'chart-grid'} x1={L} x2={W - R} y1={y(t)} y2={y(t)} />
          <text x={L - 6} y={y(t) + 4} textAnchor="end">{compact.format(t)}</text>
        </g>
      ))}
      {bars.map((s, k) => (
        <g key={s.label} fill={s.color}>
          {s.values.map((v, i) => {
            if (v === null || v === 0) return null; // una barra en cero taparía el eje
            const si = d.series.indexOf(s);
            const base = d.stacked ? stackedBase[si][i] : 0;
            const x = cx(i) - inner / 2 + (d.stacked ? 0 : k * bw);
            const y0 = y(base), y1 = y(base + v);
            return <rect key={i} className="chart-mark" x={x + 1} width={Math.max(1, bw - 2)} y={Math.min(y0, y1)} height={Math.max(1, Math.abs(y1 - y0))} rx={3} />;
          })}
        </g>
      ))}
      {lines.map((s) => {
        const si = d.series.indexOf(s);
        return (
          <g key={s.label}>
            {area && segments(s).map((pts, j) => {
              // Área: hasta la serie de abajo (apilada) o hasta el cero.
              const first = Number(pts[0].split(',')[0]), last = Number(pts[pts.length - 1].split(',')[0]);
              const idx = (px: number) => Math.round((px - L - band / 2) / band);
              const floor = d.stacked
                ? pts.map((p) => { const i = idx(Number(p.split(',')[0])); return `${cx(i).toFixed(1)},${y(stackedBase[si][i]).toFixed(1)}`; }).reverse().join(' ')
                : `${last},${y(0).toFixed(1)} ${first},${y(0).toFixed(1)}`;
              return <polygon key={`a${j}`} className="chart-area" points={`${pts.join(' ')} ${floor}`} fill={s.color} />;
            })}
            {segments(s).map((pts, j) => <polyline key={j} className="chart-line" points={pts.join(' ')} style={{ stroke: s.color }} />)}
            {s.values.map((v, i) => v === null ? null : (
              <circle key={i} className="chart-dot" cx={cx(i)} cy={y(d.stacked ? stackedBase[si][i] + v : v)} r={n > 40 ? 2.5 : 4} style={{ fill: s.color }} pointerEvents="none" />
            ))}
          </g>
        );
      })}
      {showValues && d.categories.map((_, i) => {
        if (d.stacked) {
          const total = d.series.reduce((a, s) => a + (s.values[i] ?? 0), 0);
          return <text key={`v${i}`} className="chart-value" x={cx(i)} y={y(Math.max(total, 0)) - 5} textAnchor="middle">{formatReportValue(d.series[0].column, total)}</text>;
        }
        return d.series.map((s, k) => {
          const v = s.values[i];
          if (v === null) return null;
          const x = s.line ? cx(i) : cx(i) - inner / 2 + bars.indexOf(s) * bw + bw / 2;
          return <text key={`v${i}-${k}`} className="chart-value" x={x} y={y(Math.max(v, 0)) - (s.line ? 9 : 5)} textAnchor="middle">{compact.format(v)}</text>;
        });
      })}
      {d.categories.map((c, i) => i % labelStep === 0 && (
        <text key={`x${i}`} x={cx(i)} y={H - 8} textAnchor="middle">{clip(c, 12)}</text>
      ))}
      {d.categories.map((_, i) => (
        <rect key={`h${i}`} className="chart-hit" x={L + band * i} y={T} width={band} height={plotH}><title>{tip(d, i)}</title></rect>
      ))}
    </svg>
  );
}

// ---------------- Barras horizontales ----------------

/** Barras horizontales: las etiquetas largas (proveedores, productos) se leen sin girarlas. */
function Bars({ d, W }: { d: ChartData; W: number }) {
  const cats = d.categories.slice(0, MAX_BARS);
  const grouped = !d.stacked && d.series.length > 1;
  const subH = 12;
  const rowH = grouped ? d.series.length * (subH + 2) + 10 : 26;
  const labelW = Math.min(150, Math.round(W * 0.3)), valueW = 90;
  const H = cats.length * rowH;
  const plot = W - labelW - valueW;
  const sub = { ...d, categories: cats, series: d.series.map((s) => ({ ...s, values: s.values.slice(0, cats.length) })) };
  const [lo, hi] = extent(sub);
  const sc = lo < 0 ? niceScale(lo, hi) : { min: 0, max: hi || 1 };
  const x = (v: number) => labelW + (plot * (v - sc.min)) / (sc.max - sc.min);
  const single = d.series.length === 1;
  return (
    <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary(d)}>
      {lo < 0 && <line className="chart-axis" x1={x(0)} x2={x(0)} y1={0} y2={H} />}
      {cats.map((c, i) => {
        let pos = 0, neg = 0;
        const top = i * rowH;
        const total = d.series.reduce((a, s) => a + (s.values[i] ?? 0), 0);
        return (
          <g key={i}>
            <text x={labelW - 8} y={top + rowH / 2 + 4} textAnchor="end">{clip(c, Math.max(8, Math.floor(labelW / 7)))}</text>
            {d.series.map((s, k) => {
              const v = s.values[i];
              if (v === null || v === 0) return null;
              let from = 0;
              if (d.stacked) { from = v >= 0 ? pos : neg; if (v >= 0) pos += v; else neg += v; }
              const x0 = x(from), x1 = x(from + v);
              const yy = grouped ? top + 5 + k * (subH + 2) : top + 6;
              const h = grouped ? subH : rowH - 12;
              return <rect key={k} className="chart-mark" fill={s.color} x={Math.min(x0, x1)} y={yy} width={Math.max(2, Math.abs(x1 - x0))} height={h} rx={3} />;
            })}
            {(single || d.values) && !grouped && (
              <text className="chart-value" x={x(Math.max(d.stacked ? pos : total, 0)) + 6} y={top + rowH / 2 + 4}>
                {formatReportValue(d.series[0].column, d.stacked ? total : d.series[0].values[i])}
              </text>
            )}
            {grouped && d.values && d.series.map((s, k) => s.values[i] === null ? null : (
              <text key={`v${k}`} className="chart-value" x={x(Math.max(s.values[i]!, 0)) + 6} y={top + 5 + k * (subH + 2) + 10}>{compact.format(s.values[i]!)}</text>
            ))}
            <rect className="chart-hit" x={0} y={top} width={W} height={rowH}><title>{tip(sub, i)}</title></rect>
          </g>
        );
      })}
    </svg>
  );
}

// ---------------- Pastel y dona ----------------

function Pie({ d, donut, colors: declared }: { d: ChartData; donut: boolean; colors?: string[] | null }) {
  const s = d.series[0];
  const fmt = (v: number) => formatReportValue(s.column, v);
  const slices = pieSlices(d.categories, s.values, MAX_SLICES);
  const total = slices.reduce((a, p) => a + p.value, 0);
  if (total <= 0) return null;
  const colors = slices.map((p, i) => (p.other ? 'var(--chart-other)' : seriesColor(i, declared)));
  let angle = -Math.PI / 2;
  const R = 80, r0 = donut ? 48 : 0;
  const pt = (a: number, rad: number) => `${(90 + rad * Math.cos(a)).toFixed(2)} ${(90 + rad * Math.sin(a)).toFixed(2)}`;
  const arcs = slices.map((p, i) => {
    const a0 = angle;
    const a1 = angle + (p.value / total) * Math.PI * 2;
    angle = a1;
    const large = a1 - a0 > Math.PI ? 1 : 0;
    let path: string;
    if (slices.length === 1) {
      // Una sola rebanada del 100 %: dos medias lunas (un arco de 360° no se dibuja).
      path = `M ${pt(-Math.PI / 2, R)} A ${R} ${R} 0 1 1 ${pt(Math.PI / 2, R)} A ${R} ${R} 0 1 1 ${pt(-Math.PI / 2, R)}` +
        (donut ? ` M ${pt(-Math.PI / 2, r0)} A ${r0} ${r0} 0 1 0 ${pt(Math.PI / 2, r0)} A ${r0} ${r0} 0 1 0 ${pt(-Math.PI / 2, r0)}` : '') + ' Z';
    } else if (donut) {
      path = `M ${pt(a0, R)} A ${R} ${R} 0 ${large} 1 ${pt(a1, R)} L ${pt(a1, r0)} A ${r0} ${r0} 0 ${large} 0 ${pt(a0, r0)} Z`;
    } else {
      path = `M 90 90 L ${pt(a0, R)} A ${R} ${R} 0 ${large} 1 ${pt(a1, R)} Z`;
    }
    return { path, p, color: colors[i] };
  });
  const pct = (v: number) => `${((v / total) * 100).toFixed(d.values ? 1 : 0)} %`;
  return (
    <div className="pie-wrap">
      <svg className="chart" viewBox="0 0 180 180" role="img" aria-label={summary(d)}>
        {arcs.map((a) => (
          <path key={a.p.label} d={a.path} fill={a.color} stroke="var(--surface)" strokeWidth={2} fillRule="evenodd">
            <title>{`${a.p.label}: ${fmt(a.p.value)} (${((a.p.value / total) * 100).toFixed(1)} %)`}</title>
          </path>
        ))}
      </svg>
      <ul className="chart-legend" aria-label="Leyenda">
        {arcs.map((a) => (
          <li key={a.p.label}><span className="chart-swatch" style={{ background: a.color }} aria-hidden />{a.p.label} · {pct(a.p.value)}{d.values ? ` · ${fmt(a.p.value)}` : ''}</li>
        ))}
      </ul>
    </div>
  );
}

// ---------------- Indicador ----------------

/** H4: valor del último periodo y su cambio contra el anterior, con flecha y texto (nunca solo color). */
function KpiView({ result, measure }: { result: ReportResult; measure?: number }) {
  const k = kpiOf(result, measure ?? result.chartOptions?.measures?.[0] ?? 0);
  if (!k) return null;
  const fmt = (v: number | null | undefined) => formatReportValue(k.column, v);
  const Icon = k.direction === 'up' ? ArrowUpRight : k.direction === 'down' ? ArrowDownRight : Minus;
  return (
    <div className="kpi">
      <div className="kpi-value">{fmt(k.value)}</div>
      {k.label && <div className="kpi-meta">{k.column.label} · {k.label}</div>}
      {result.comparison && (k.previous === null || k.previous === undefined ? (
        <div className="kpi-delta">Sin dato del periodo anterior{k.previousLabel ? ` (${k.previousLabel})` : ''}</div>
      ) : (
        <div className="kpi-delta" data-direction={k.direction ?? undefined}>
          <Icon size={16} aria-hidden />
          <span>
            {k.change !== null
              ? `${k.change > 0 ? '+' : k.change < 0 ? '−' : ''}${Math.abs(k.change).toLocaleString('es-MX', { maximumFractionDigits: 1 })} %`
              : k.direction === 'flat' ? 'Sin cambio' : k.direction === 'up' ? 'Sube' : 'Baja'}
            {` vs ${k.previousLabel} (${fmt(k.previous)})`}
          </span>
        </div>
      ))}
    </div>
  );
}
