import type { ReportColumn, ReportResult } from '../api/types';

/** Formato de un valor de reporte según su columna (números con la escala declarada). */
export function formatReportValue(c: ReportColumn, v: unknown): string {
  if (v === null || v === undefined || v === '') return '—';
  if (c.type === 'number' && typeof v === 'number') {
    const s = c.scale ?? 2;
    return v.toLocaleString('es-MX', { minimumFractionDigits: s, maximumFractionDigits: s });
  }
  return String(v);
}

const MAX_SLICES = 8; // la paleta categórica tiene 8 tonos; el resto se agrupa en «Otros»
const MAX_BARS = 15;

interface Point { label: string; value: number }

function points(r: ReportResult, measure: number): { points: Point[]; group: ReportColumn; m: ReportColumn } | null {
  const group = r.columns.find((c) => c.kind === 'group');
  const m = r.columns.filter((c) => c.kind === 'measure')[measure];
  if (!group || !m || m.type !== 'number') return null;
  return { group, m, points: r.rows.map((row) => ({ label: String(row[group.key] ?? '—'), value: Number(row[m.key] ?? 0) })) };
}

/**
 * Gráfica de un reporte (spec 007) en SVG propio: barras horizontales, línea o pastel. Una sola serie (la medida
 * elegida); el título la nombra, así que no lleva leyenda salvo el pastel. Cada marca tiene su valor en un
 * <title> (se ve al pasar el cursor) y el conjunto un resumen para lectores de pantalla; la tabla va debajo.
 */
export function ReportChart({ result, kind, measure = 0 }: { result: ReportResult; kind: 'bar' | 'line' | 'pie'; measure?: number }) {
  const data = points(result, measure);
  if (!data || data.points.length === 0) return null;
  const { group, m } = data;
  const fmt = (v: number) => formatReportValue(m, v);
  const summary = `${m.label} por ${group.label.toLowerCase()}: ${data.points.slice(0, 5).map((p) => `${p.label} ${fmt(p.value)}`).join('; ')}${data.points.length > 5 ? '…' : ''}`;

  if (kind === 'pie') {
    const sorted = [...data.points].filter((p) => p.value > 0);
    const slices = sorted.length > MAX_SLICES
      ? [...sorted.slice(0, MAX_SLICES - 1), { label: 'Otros', value: sorted.slice(MAX_SLICES - 1).reduce((a, p) => a + p.value, 0) }]
      : sorted;
    const total = slices.reduce((a, p) => a + p.value, 0);
    if (total <= 0) return null;
    let angle = -Math.PI / 2;
    const R = 80, r0 = 48;
    const arcs = slices.map((p, i) => {
      const a0 = angle;
      const a1 = angle + (p.value / total) * Math.PI * 2;
      angle = a1;
      const large = a1 - a0 > Math.PI ? 1 : 0;
      const pt = (a: number, rad: number) => `${(90 + rad * Math.cos(a)).toFixed(2)} ${(90 + rad * Math.sin(a)).toFixed(2)}`;
      // Una sola rebanada del 100 %: dos medias lunas (un arco de 360° no se dibuja).
      const d = slices.length === 1
        ? `M ${pt(-Math.PI / 2, R)} A ${R} ${R} 0 1 1 ${pt(Math.PI / 2, R)} A ${R} ${R} 0 1 1 ${pt(-Math.PI / 2, R)} M ${pt(-Math.PI / 2, r0)} A ${r0} ${r0} 0 1 0 ${pt(Math.PI / 2, r0)} A ${r0} ${r0} 0 1 0 ${pt(-Math.PI / 2, r0)} Z`
        : `M ${pt(a0, R)} A ${R} ${R} 0 ${large} 1 ${pt(a1, R)} L ${pt(a1, r0)} A ${r0} ${r0} 0 ${large} 0 ${pt(a0, r0)} Z`;
      return { d, p, color: `var(--chart-${i + 1})` };
    });
    return (
      <div className="pie-wrap">
        <svg className="chart" viewBox="0 0 180 180" role="img" aria-label={summary}>
          {arcs.map((a) => (
            <path key={a.p.label} d={a.d} fill={a.color} stroke="var(--surface)" strokeWidth={2} fillRule="evenodd">
              <title>{`${a.p.label}: ${fmt(a.p.value)} (${((a.p.value / total) * 100).toFixed(1)} %)`}</title>
            </path>
          ))}
        </svg>
        <ul className="chart-legend" aria-hidden>
          {arcs.map((a) => (
            <li key={a.p.label}><span className="chart-swatch" style={{ background: a.color }} />{a.p.label} · {((a.p.value / total) * 100).toFixed(0)} %</li>
          ))}
        </ul>
      </div>
    );
  }

  if (kind === 'line') {
    const pts = data.points;
    const W = 560, H = 200, L = 8, B = 24, T = 12, Rr = 8;
    const max = Math.max(...pts.map((p) => p.value), 0) || 1;
    const min = Math.min(...pts.map((p) => p.value), 0);
    const x = (i: number) => L + (pts.length === 1 ? (W - L - Rr) / 2 : (i * (W - L - Rr)) / (pts.length - 1));
    const y = (v: number) => T + (H - T - B) * (1 - (v - min) / (max - min || 1));
    const step = Math.max(1, Math.ceil(pts.length / 8));
    return (
      <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary}>
        <line className="chart-axis" x1={L} x2={W - Rr} y1={H - B} y2={H - B} />
        <polyline className="chart-line" points={pts.map((p, i) => `${x(i)},${y(p.value)}`).join(' ')} />
        {pts.map((p, i) => (
          <g key={i}>
            <circle className="chart-hit" cx={x(i)} cy={y(p.value)} r={12}><title>{`${p.label}: ${fmt(p.value)}`}</title></circle>
            <circle className="chart-dot" cx={x(i)} cy={y(p.value)} r={4} pointerEvents="none" />
            {i % step === 0 && <text x={x(i)} y={H - 6} textAnchor="middle">{p.label}</text>}
          </g>
        ))}
      </svg>
    );
  }

  // Barras horizontales: las etiquetas largas (proveedores, productos) se leen sin girarlas.
  const bars = data.points.slice(0, MAX_BARS);
  const max = Math.max(...bars.map((p) => p.value), 0) || 1;
  const rowH = 26, labelW = 150, valueW = 90, W = 560;
  const H = bars.length * rowH;
  const plot = W - labelW - valueW;
  return (
    <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary}>
      {bars.map((p, i) => {
        const w = Math.max(2, (Math.max(0, p.value) / max) * plot);
        const label = p.label.length > 22 ? `${p.label.slice(0, 21)}…` : p.label;
        return (
          <g key={i}>
            <text x={labelW - 8} y={i * rowH + 17} textAnchor="end">{label}</text>
            <rect className="chart-hit" x={labelW} y={i * rowH} width={plot} height={rowH}><title>{`${p.label}: ${fmt(p.value)}`}</title></rect>
            <rect className="chart-bar" x={labelW} y={i * rowH + 6} width={w} height={rowH - 12} rx={4} pointerEvents="none" />
            <text x={labelW + w + 6} y={i * rowH + 17}>{fmt(p.value)}</text>
          </g>
        );
      })}
    </svg>
  );
}
