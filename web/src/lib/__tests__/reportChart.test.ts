import { describe, expect, it } from 'vitest';
import type { ReportColumn, ReportResult } from '../../api/types';
import { buildChart, extent, fillPeriods, formatPeriod, kpiOf, niceScale, parsePeriod, PALETTE, pieSlices, seriesColor, valueLabelsFit } from '../reportChart';

const g = (key: string, label: string, period?: ReportColumn['period']): ReportColumn => ({ key, label, kind: 'group', type: 'text', period });
const m = (key: string, label: string, fn = 'sum'): ReportColumn => ({ key, label, kind: 'measure', type: 'number', scale: 2, fn });
const result = (columns: ReportColumn[], rows: ReportResult['rows'], extra: Partial<ReportResult> = {}): ReportResult =>
  ({ entity: 'E', name: 'R', label: 'R', columns, rows, totals: {}, truncated: false, ...extra });

describe('periodos (039 H5 CA3, H2 CA3)', () => {
  it('semana ISO en el cambio de año, ida y vuelta', () => {
    for (const label of ['2026-S01', '2026-S53', '2027-S01', '2020-S53', '2026-S41']) expect(formatPeriod(parsePeriod(label, 'week')!, 'week')).toBe(label);
    expect(new Date(parsePeriod('2026-S01', 'week')!).toISOString().slice(0, 10)).toBe('2025-12-29');
    expect(formatPeriod(Date.UTC(2027, 0, 1), 'week')).toBe('2026-S53');
    expect(formatPeriod(parsePeriod('2026-T4', 'quarter')!, 'quarter')).toBe('2026-T4');
    expect(parsePeriod('(sin acceso)', 'month')).toBeNull();
    // Revisión adversarial 039 (sospecha): Date.UTC toma el año 26 como 1926.
    expect(formatPeriod(parsePeriod('0026-03', 'month')!, 'month')).toBe('0026-03');
  });

  it('rellena los huecos en orden y deja al final los grupos que no son fecha', () => {
    expect(fillPeriods(['2026-S52', '2027-S02', '(sin valor)'], 'week')).toEqual(['2026-S52', '2026-S53', '2027-S01', '2027-S02', '(sin valor)']);
    expect(fillPeriods(['2026-T3', '2027-T1'], 'quarter')).toEqual(['2026-T3', '2026-T4', '2027-T1']);
    expect(fillPeriods(['2026-01', '2026-02'], 'month')).toBeNull(); // nada que rellenar
    expect(fillPeriods(['2026-03', '2026-01'], 'month')).toBeNull(); // ordenado por la medida: no se toca
  });

  it('relleno con tope', () => {
    expect(fillPeriods(['2000-01-01', '2026-01-01'], 'day')).toBeNull();
    expect(fillPeriods(['2000-01-01', '2000-01-03'], 'day', 2)).toBeNull();
  });
});

describe('series (039 H2)', () => {
  it('dos agrupaciones: la segunda da las series; los huecos de una suma valen 0', () => {
    const r = result([g('g0', 'Mes', 'month'), g('g1', 'Proveedor'), m('m0', 'Monto')], [
      { g0: '2026-01', g1: 'Acme', m0: 10 }, { g0: '2026-01', g1: 'Beta', m0: 30 }, { g0: '2026-03', g1: 'Acme', m0: 5 },
    ], { chartOptions: { stacked: true, colors: ['lima'] } });
    const d = buildChart(r, 'column')!;
    expect(d.categories).toEqual(['2026-01', '2026-02', '2026-03']);
    expect(d.series.map((s) => [s.label, s.values])).toEqual([['Beta', [30, 0, 0]], ['Acme', [10, 0, 5]]]);
    expect(d.series[0].color).toBe('var(--chart-12)'); // lima, el color declarado de la primera serie
    expect(d.series[1].color).toBe('var(--chart-2)'); // sin color declarado: su lugar fijo
    expect(d.stacked).toBe(true);
    expect(d.legend).toBe(true);
    expect(extent(d)).toEqual([0, 40]);
  });

  it('Otros: más de 12 series se juntan sin perder el total', () => {
    const rows = Array.from({ length: 15 }, (_, i) => ({ g0: 'X', g1: `P${i + 1}`, m0: i + 1 }));
    const d = buildChart(result([g('g0', 'Mes'), g('g1', 'Proveedor'), m('m0', 'Monto')], rows), 'bar')!;
    expect(d.series).toHaveLength(12);
    expect(d.series[11].label).toBe('Otros');
    expect(d.series[11].values).toEqual([1 + 2 + 3 + 4]);
    expect(d.series[11].color).toBe('var(--chart-other)');
    expect(d.series.reduce((a, s) => a + (s.values[0] ?? 0), 0)).toBe(120);
  });

  it('varias medidas: la combinada marca las líneas; un promedio sin datos queda vacío, no en 0', () => {
    const r = result([g('g0', 'Mes', 'month'), m('m0', 'Monto'), m('m1', 'Promedio', 'avg')], [
      { g0: '2026-01', m0: 10, m1: 5 }, { g0: '2026-03', m0: 20, m1: 7 },
    ], { chartOptions: { measures: [0, 1], lines: [1] } });
    const d = buildChart(r, 'combo')!;
    expect(d.series.map((s) => s.line)).toEqual([false, true]);
    expect(d.series[0].values).toEqual([10, 0, 20]);
    expect(d.series[1].values).toEqual([5, null, 7]);
    expect(buildChart(r, 'combo', 1)!.series).toHaveLength(1); // la tarjeta elige una medida
  });

  it('una sola serie no lleva leyenda; un color fuera de la paleta se ignora', () => {
    const d = buildChart(result([g('g0', 'Cliente'), m('m0', 'Monto')], [{ g0: 'A', m0: 1 }], { chartOptions: { colors: ['url(x)'] } }), 'bar')!;
    expect(d.legend).toBe(false);
    expect(d.series[0].color).toBe('var(--chart-1)');
    expect(seriesColor(0, ['purpura'])).toBe('var(--chart-11)');
    expect(PALETTE).toHaveLength(12);
  });

  // Regresión (revisión exploratoria 039, F-01): diez meses de columnas y línea en una tarjeta de ~300 px encimaban los valores.
  it('039 F-01: los valores solo se escriben si caben en el ancho de la categoría', () => {
    expect(valueLabelsFit(25, 2, 10)).toBe(false); // combinada angosta: 2 etiquetas en 25 px
    expect(valueLabelsFit(90, 2, 10)).toBe(true); // la misma, a lo ancho
    expect(valueLabelsFit(200, 1, 30)).toBe(false); // demasiadas marcas aunque quepan
  });

  // Regresión (revisión exploratoria 039, F-02): las rebanadas iban en el orden de las filas; «Otros» juntaba las últimas.
  it('039 F-02: el pastel va de mayor a menor y junta en «Otros» las menores', () => {
    const s = pieSlices(['A', 'B', 'C', 'D'], [1, 50, 3, 20], 3);
    expect(s.map((x) => [x.label, x.value, x.other])).toEqual([['B', 50, false], ['D', 20, false], ['Otros', 4, true]]);
    expect(pieSlices(['A', 'B'], [0, 5], 8).map((x) => x.label)).toEqual(['B']); // sin rebanadas vacías
  });

  it('escala con el cero y marcas redondas', () => {
    expect(niceScale(0, 87)).toEqual({ min: 0, max: 100, step: 25 });
    expect(niceScale(-12, 30)).toEqual({ min: -20, max: 40, step: 20 });
    expect(niceScale(0, 0)).toEqual({ min: 0, max: 1, step: 0.25 });
  });
});

describe('indicador (039 H4)', () => {
  const cols = [g('g0', 'Mes', 'month'), m('m0', 'Monto')];
  it('cambio contra el periodo anterior', () => {
    const k = kpiOf(result(cols, [], { comparison: { current: { label: '2026-10', values: { m0: 120 } }, previous: { label: '2026-09', values: { m0: 100 } } } }))!;
    expect(k).toMatchObject({ value: 120, previous: 100, change: 20, direction: 'up', label: '2026-10', previousLabel: '2026-09' });
  });
  it('anterior en cero: dirección sí, porcentaje no', () => {
    const k = kpiOf(result(cols, [], { comparison: { current: { label: '2026-10', values: { m0: 50 } }, previous: { label: '2026-09', values: { m0: 0 } } } }))!;
    expect(k.change).toBeNull();
    expect(k.direction).toBe('up');
  });
  it('sin comparación, el total', () => {
    expect(kpiOf(result([m('m0', 'Monto')], [], { totals: { m0: 7 } }))).toMatchObject({ value: 7, change: null, direction: null });
  });
});
